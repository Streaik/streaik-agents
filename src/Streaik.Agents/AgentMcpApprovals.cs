using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Agents;

internal sealed class NativeResponseContent(ResponseInputItem item) : AIContent
{
    public ResponseInputItem Item { get; } = item;
}

internal sealed class McpReviewContent(McpResultReviews.Result review) : AIContent
{
    public McpResultReviews.Result Review { get; } = review;
}

internal sealed class AgentMcpApprovals(bool ui, string? nativeScope, string? reviewScope, ResponseToolDefinition[] tools,
    McpApprovalDecisions decisions, McpResultReviews reviews, HostedToolAccess? access)
{
    private readonly HashSet<string> replayed = new(StringComparer.Ordinal);

    public void ValidateNative(IEnumerable<ResponseMcpApprovalResponseItem> items)
    {
        var replies = items.ToArray();
        if (replies.Length == 0) return;
        if (nativeScope is null || replies.Any(item => item.ApprovalRequestId is not { Length: > 0 and <= 256 } || item.Approve is null)
            || !decisions.Consume(nativeScope, replies.Select(item => item.ApprovalRequestId!).ToArray()))
            throw new AgentRequestException("Unknown, unauthorized, or already used MCP approval.", "mcp_approval_invalid");
    }

    public async Task<List<AIContent>> ObserveAsync(ResponseResult response, CancellationToken ct)
    {
        var approvals = response.OutputItems.OfType<ResponseMcpApprovalRequestItem>().ToArray();
        if (approvals.Length == 0) return [];
        if (approvals.Any(item => item.Id is not { Length: > 0 and <= 256 }))
            throw new InvalidOperationException("The upstream returned an invalid MCP approval ID.");
        if (!ui)
        {
            if (nativeScope is not null) decisions.Observe(nativeScope, approvals.Select(item => item.Id!));
            return approvals.Select(item => (AIContent)new NativeResponseContent(item)).ToList();
        }
        if (reviewScope is null || access is null)
            throw new AgentRequestException("MCP review requires an authenticated caller, conversation ID, and authorized MCP access.", "mcp_review_not_authorized");
        var approvalIds = approvals.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (response.OutputItems.OfType<ResponseMcpCallItem>().Any(item => item.ApprovalRequestId is { } id && approvalIds.Contains(id)))
            throw new InvalidOperationException("The upstream returned a call together with its approval request.");
        List<AIContent> result = [];
        foreach (var approval in approvals.DistinctBy(item => item.Id))
        {
            if (approval.ServerLabel is not { Length: > 0 } server || approval.Name is not { Length: > 0 } name
                || approval.Arguments is not { Length: > 0 and <= 65536 } args)
                throw new InvalidOperationException("The upstream returned an invalid MCP approval request.");
            var descriptor = Descriptor(server, name);
            using var arguments = JsonDocument.Parse(args, new JsonDocumentOptions { AllowDuplicateProperties = false });
            result.Add(new McpReviewContent(await reviews.ExecuteAsync(reviewScope, approval.Id!, descriptor, name, arguments.RootElement, access, ct, nativeScope)));
        }
        return result;
    }

    public static bool IsReview(ToolInvocationPart part) => part.ToolName == McpResultReviews.ToolName || part.Type == $"tool-{McpResultReviews.ToolName}";

    public NativeResponseContent Replay(Role role, ToolInvocationPart part)
    {
        if (role != Role.assistant || reviewScope is null || part.ProviderExecuted != true
            || part.Approval?.Approved is not { } approved || part.Approval.Id != part.ToolCallId
            || (approved ? part.State is not ("approval-responded" or "output-available") : part.State is not ("approval-responded" or "output-denied"))
            || part.Input is not JsonElement input || !replayed.Add(part.ToolCallId))
            throw new AgentRequestException("Invalid MCP result review.", "mcp_approval_invalid");
        try
        {
            var result = reviews.Resolve(reviewScope, part.ToolCallId, input, approved, approved ? part.Output as JsonElement? : null,
                (server, name, _) => Descriptor(server, name).ServerUrl.AbsoluteUri);
            return new(new ResponseMcpCallItem { Id = $"mcp_{result.Id}", ServerLabel = result.Server, Name = result.Name,
                Arguments = result.Input.GetRawText(), Status = approved ? "completed" : "failed",
                Output = result.Output is { } output ? JsonSerializer.SerializeToElement(McpResultReviews.OutputText(output)) : null,
                Error = approved ? null : JsonSerializer.SerializeToElement("The user chose not to disclose the tool result.") });
        }
        catch (UnauthorizedAccessException) { throw new AgentRequestException("Unknown or invalid MCP result review.", "mcp_approval_invalid"); }
    }

    private McpToolDefinition Descriptor(string server, string name) => McpResultReviews.FromNative(
        tools.Select(tool => tool.Extra).OfType<Dictionary<string, JsonElement>>(), server, name);
}

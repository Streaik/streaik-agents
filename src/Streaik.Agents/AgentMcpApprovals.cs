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

internal sealed class AgentMcpApprovals(bool ui, string? nativeScope, string? reviewScope,
    McpApprovalDecisions decisions, McpResultReviews reviews, ResponseToolDefinition[] tools, AgentExecutionApproval execution)
{
    private readonly HashSet<string> replayed = new(StringComparer.Ordinal);
    internal AgentExecutionApproval? Execution => ui ? execution : null;

    public void ValidateNative(IEnumerable<ResponseMcpApprovalResponseItem> items)
    {
        var replies = items.ToArray();
        if (replies.Length == 0) return;
        if (nativeScope is null || replies.Any(item => item.ApprovalRequestId is not { Length: > 0 and <= 256 } || item.Approve is null)
            || !decisions.Consume(nativeScope, replies.Select(item => item.ApprovalRequestId!).ToArray()))
            throw new AgentRequestException("Unknown, unauthorized, or already used MCP approval.", "mcp_approval_invalid");
    }

    public List<AIContent> Observe(string? model, ResponseResult response)
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
        var approvalIds = approvals.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (response.OutputItems.OfType<ResponseMcpCallItem>().Any(item => item.ApprovalRequestId is { } id && approvalIds.Contains(id)))
            throw new InvalidOperationException("The upstream returned a call together with its approval request.");
        List<AIContent> result = [];
        foreach (var approval in approvals.DistinctBy(item => item.Id))
        {
            if (approval.ServerLabel is not { Length: > 0 } server || approval.Name is not { Length: > 0 } name
                || approval.Arguments is not { Length: > 0 and <= 65536 } args)
                throw new InvalidOperationException("The upstream returned an invalid MCP approval request.");
            if (tools.Any(tool => tool.Extra?.GetValueOrDefault("server_label") is { ValueKind: JsonValueKind.String } label && label.ValueEquals(server)
                && tool.Extra?.GetValueOrDefault("require_approval") is { ValueKind: JsonValueKind.String } policy && policy.ValueEquals("never")))
                throw new AgentRequestException("The provider requested approval for an MCP server with approval disabled.", "mcp_approval_invalid");
            using var arguments = JsonDocument.Parse(args, new JsonDocumentOptions { AllowDuplicateProperties = false });
            result.Add(execution.Issue(model, new("native", server, name, approval.Id!, args,
                AgentExecutionApproval.Proof(response, AgentExecutionApproval.ProofsKey, approval.Id!)), arguments.RootElement.Clone()));
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

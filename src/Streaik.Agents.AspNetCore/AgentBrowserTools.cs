using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Streaik.Tools;
using Streaik.Vercel.Models;
using Streaik.Vercel.Security;

namespace Streaik.Agents.AspNetCore;

internal sealed class AgentBrowserTools
{
    private static readonly string Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly Dictionary<string, BrowserFunction> declarations = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    public ToolApprovalSigner? Signer { get; }
    public ChatOptions? Options => declarations.Count == 0 ? null : new ChatOptions { Tools = declarations.Values.Cast<AITool>().ToList() };

    public AgentBrowserTools(HttpContext context, AgentChatRequest request, IReadOnlyList<Agent> agents)
    {
        if (request.Tools is not { Length: > 0 } && !(request.Messages?.Any(message => message?.Parts?.OfType<ToolInvocationPart>().Any() == true) ?? false))
            return;
        if (agents.Count != 1) throw new AgentRequestException("Browser tools are supported only in single-agent chat.");
        if (request.Id is not { Length: > 0 and <= 256 }) throw new AgentRequestException("Browser tools require a conversation ID.");
        if (request.Tools is { Length: > 128 }) throw new AgentRequestException("Too many browser tool declarations.");
        var subject = McpResultReviews.Subject(context.User);
        if (context.User.Identity?.IsAuthenticated == true && subject is null)
            throw new AgentRequestException("Tool approval signing is unavailable for this caller.");
        Signer = new ToolApprovalSigner(Secret, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { subject, request.Id, agent = agents[0] }, Json))));
        foreach (var tool in request.Tools ?? [])
        {
            if (tool?.Name is not { Length: > 0 and <= 64 } name || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                || name == McpResultReviews.ToolName || name.StartsWith("streaik_", StringComparison.Ordinal) || tool.InputSchema is null)
                throw new AgentRequestException("Invalid browser function name or schema.");
            if (tool.InputSchema.AdditionalProperties?.Keys.Any(HostedToolsJson.IsHostedKind) == true)
                throw new AgentRequestException("Configure hosted tools in the agent definition, not in the browser tool list.");
            if (!declarations.TryAdd(name, new(name, tool.Description, JsonSerializer.SerializeToElement(tool.InputSchema, Json))))
                throw new AgentRequestException("Duplicate browser function name.");
        }
        try
        {
            Signer.ValidateIncoming((request.Messages ?? []).Where(message => message?.Parts is not null).Select(message => new UIMessage
            {
                Id = message.Id, Role = message.Role, Parts = message.Parts.Where(part => part is not ToolInvocationPart invocation
                    || !AgentMcpApprovals.IsReview(invocation)).ToList()
            }));
        }
        catch (ToolApprovalSignatureException error) { throw new AgentRequestException(error.Message); }
    }

    public void Replay(Role role, ToolInvocationPart tool, List<AIContent> parts)
    {
        var name = tool.ToolName ?? (tool.Type.StartsWith("tool-", StringComparison.Ordinal) ? tool.Type[5..] : null);
        if (role != Role.assistant || tool.ProviderExecuted == true || name is null || !declarations.ContainsKey(name)
            || tool.ToolCallId is not { Length: > 0 and <= 256 } id || !seen.Add(id)
            || tool.Input is not JsonElement { ValueKind: JsonValueKind.Object } input || tool.Approval?.Approved is not { } approved)
            throw new AgentRequestException("Invalid browser tool approval continuation.");
        var arguments = input.Deserialize<Dictionary<string, object?>>(Json)!;
        if (!approved && tool.State is "approval-responded" or "output-denied")
        {
            parts.Add(new FunctionCallContent(id, name, arguments));
            parts.Add(new FunctionResultContent(id, "The user declined to share this tool result."));
        }
        else if (approved && tool.State is "output-available" or "output-error" or "approval-responded"
            && (tool.Output is not null || tool.ErrorText is not null))
        {
            parts.Add(new FunctionCallContent(id, name, arguments));
            parts.Add(new FunctionResultContent(id, tool.ErrorText ?? tool.Output));
        }
        else throw new AgentRequestException("Browser tools require a completed, reviewed result before continuing.");
    }
}

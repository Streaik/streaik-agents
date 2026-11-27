using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;
using Streaik.Vercel.Security;

namespace Streaik.Agents;

internal sealed class AgentExecutionApproval(string? scope, ToolApprovalSigner? signer, AgentHostedTools tools)
{
    internal const string ProofsKey = "streaik_mcp_approvals";
    internal const string HistoryKey = "streaik_mcp_history";
    internal sealed record Call(string Kind, string Server, string Name, string CallId, string? Arguments = null, string? Proof = null);
    internal sealed record Receipt(Call Call, bool Approved, string? HistoryProof = null);
    internal sealed class Request(IReadOnlyList<UIMessagePart> parts) : AIContent
    {
        public IReadOnlyList<UIMessagePart> Parts { get; } = parts;
    }
    internal sealed class Decision(ToolInvocationPart part, Role role) : AIContent
    {
        public ToolInvocationPart Part { get; } = part;
        public Role Role { get; } = role;
    }

    private readonly HostedToolContinuation wire = new(signer);
    internal static bool IsContinuation(ToolInvocationPart part) => HostedToolContinuation.IsApproval(part) || HostedToolContinuation.IsResult(part);

    internal Request Issue(string? model, Call call, JsonElement input)
    {
        try
        {
            _ = Descriptor(call);
            return new(wire.Request(Scope(model), call.Name, call.Server, input, call));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException) { throw Invalid(); }
    }

    internal (Call Call, JsonElement Input, bool Approved, McpToolDefinition Descriptor) ReadDecision(string? model, Decision decision)
    {
        try
        {
            var (call, input, approved) = wire.ReadDecision<Call>(decision.Role, decision.Part, Scope(model));
            return (call, input, approved, Descriptor(call));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException or ToolApprovalSignatureException)
        { throw Invalid(); }
    }

    internal (Receipt Receipt, JsonElement Input, JsonElement Output) ReadResult(string? model, Decision decision)
    {
        try
        {
            var result = wire.ReadResult<Receipt>(decision.Role, decision.Part, Scope(model));
            _ = Descriptor(result.Receipt.Call);
            return result;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException or ToolApprovalSignatureException)
        { throw Invalid(); }
    }

    internal Request Complete(string? model, string id, JsonElement input, Receipt receipt, JsonElement output) =>
        new(wire.Complete(Scope(model), id, receipt.Call.Name, input, receipt, output));

    private McpToolDefinition Descriptor(Call call)
    {
        if (call is not { Server.Length: > 0 and <= 256, Name.Length: > 0 and <= 256, CallId.Length: > 0 and <= 256 }) throw Invalid();
        McpToolDefinition? descriptor = null;
        if (call.Kind == "native" && tools.NativeMcp.Any(tool => tool.Extra?.GetValueOrDefault("server_label") is { ValueKind: JsonValueKind.String } label && label.ValueEquals(call.Server)))
            descriptor = McpResultReviews.FromNative(tools.NativeMcp.Select(tool => tool.Extra).OfType<Dictionary<string, JsonElement>>(), call.Server, call.Name);
        else if (call.Kind is "local" or "native")
            descriptor = (call.Kind == "local" ? tools.Client : tools.Server).OfType<McpToolDefinition>()
                .SingleOrDefault(server => server.ServerLabel == call.Server);
        if (descriptor is null || !descriptor.Allows(ToolCaller.Direct) || descriptor.RequireApproval is McpApprovalPolicy.Never) throw Invalid();
        return descriptor;
    }

    private string Scope(string? model) => scope is { } value && !string.IsNullOrWhiteSpace(model) ? $"agent-api-chat:{value}:{model}"
        : throw new AgentRequestException("MCP execution approval requires a conversation ID.", "mcp_approval_not_authorized");

    internal static string? Proof(ResponseResult result, string key, string id) => result.Metadata?.GetValueOrDefault(key) is { } metadata
        && JsonSerializer.SerializeToElement(metadata, ResponseJson.Default) is { ValueKind: JsonValueKind.Object } map
        && map.TryGetProperty(id, out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;

    internal static AgentRequestException Invalid() => new("Unknown or invalid MCP execution approval.", "mcp_approval_invalid");
}

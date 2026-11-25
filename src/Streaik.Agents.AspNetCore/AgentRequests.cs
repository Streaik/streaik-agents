using System.Text.Json.Serialization;
using Streaik.Responses;
using Streaik.Vercel.Models;

namespace Streaik.Agents.AspNetCore;

internal sealed record AgentRequestBodyMetadata(Type Type);

// Agent routing belongs to this API, not the gateway's provider-facing request.
internal class AgentSelection
{
    public string? Model { get; init; }
    public IReadOnlyList<string?>? Models { get; init; }
    public AgentDefinition? Agent { get; init; }
    public IReadOnlyList<AgentDefinition?>? Agents { get; init; }
    public string? WorkflowType { get; init; }
    public WorkflowMetadata? WorkflowMetadata { get; init; }
    [JsonPropertyName("workflow_type")]
    public string? WorkflowTypeSnakeCase { get; init; }
    [JsonPropertyName("workflow_metadata")]
    public WorkflowMetadata? WorkflowMetadataSnakeCase { get; init; }
}

internal sealed class AgentChatRequest : AgentSelection
{
    public required UIMessage[] Messages { get; init; }
}

internal sealed class AgentResponsesRequest : AgentSelection
{
    public required ResponseInput Input { get; init; }
    public bool? Stream { get; init; }
    public string? Instructions { get; init; }
    public double? Temperature { get; init; }
    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }
    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }
}

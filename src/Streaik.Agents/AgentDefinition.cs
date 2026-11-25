using System.Text.Json.Serialization;

namespace Streaik.Agents;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AgentDefinition
{
    internal const string DefaultTransport = "v1/responses";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("model")]
    public AgentModel? Model { get; init; }

    [JsonPropertyName("transport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transport { get; init; } = DefaultTransport;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AgentModel
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

public sealed class AgentRequestException(string message, string code = "invalid_request") : Exception(message)
{
    public string Code { get; } = code;
}

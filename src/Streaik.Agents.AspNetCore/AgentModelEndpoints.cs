using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Streaik.Agents.AspNetCore;

internal static class AgentModelEndpoints
{
    private const string ModelPath = "/v1/models";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ModelPath, ListAsync)
            .WithName("ListAgentModels").WithTags("OpenAI").WithDescription("List agent models without their definitions.")
            .Produces<AgentModelList>().Produces(503);
        endpoints.MapGet(ModelPath + "/{**modelId}", RetrieveAsync)
            .WithName("RetrieveAgentModel").WithTags("OpenAI").WithDescription("Retrieve one agent model and its definition.")
            .Produces<AgentModelEntry>().Produces(404).Produces(503);
    }

    private static async Task<IResult> ListAsync(AgentCatalog catalog, CancellationToken ct)
    {
        try
        {
            var agents = await catalog.ListAsync(ct);
            return Results.Json(new AgentModelList(agents.Select(agent => Project(agent, detail: false)).ToArray()));
        }
        catch (AgentCatalogUnavailableException) { return CatalogUnavailable(); }
    }

    private static async Task<IResult> RetrieveAsync(string modelId, HttpContext context, AgentCatalog catalog, CancellationToken ct)
    {
        // Use the raw path so a slash and a literal escaped sequence are both decoded exactly once.
        var rawPath = context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?', 2)[0];
        var start = rawPath?.IndexOf(ModelPath + "/", StringComparison.OrdinalIgnoreCase) ?? -1;
        var id = start < 0 ? modelId : Uri.UnescapeDataString(rawPath![(start + ModelPath.Length + 1)..]);
        try
        {
            return await catalog.FindAsync(id, ct) is { } agent ? Results.Json(Project(agent, detail: true)) : Results.NotFound(new
            {
                error = new { message = $"Model '{id}' was not found.", type = "invalid_request_error", param = "model", code = "model_not_found" }
            });
        }
        catch (AgentCatalogUnavailableException) { return CatalogUnavailable(); }
    }

    private static IResult CatalogUnavailable() => Results.Json(new
    {
        error = new { code = "agent_catalog_unavailable", message = "The agent catalog is unavailable." }
    }, statusCode: 503);

    private static AgentModelEntry Project(AgentDefinition agent, bool detail)
    {
        var tags = agent.Tags;
        if (!detail && !string.IsNullOrWhiteSpace(agent.Transcription?.Id))
            tags = (tags ?? []).Append("real-time").Distinct(StringComparer.Ordinal).ToArray();
        return new AgentModelEntry(agent.Name!, agent.Created, agent.OwnedBy?.Trim() ?? "", detail ? null : agent.Name,
            agent.Description, tags, detail ? PublishDefinition(agent) : null);
    }

    private static AgentDefinition PublishDefinition(AgentDefinition agent)
    {
        // Project away known credential headers without modifying the cached source definition.
        var json = JsonSerializer.SerializeToNode(agent, JsonSerializerOptions.Web)!;
        return RemoveCredentialHeaders(json) ? json.Deserialize<AgentDefinition>(JsonSerializerOptions.Web)! : agent;
    }

    private static bool RemoveCredentialHeaders(JsonNode? value)
    {
        var changed = false;
        if (value is JsonObject obj)
            foreach (var (name, node) in obj)
            {
                if (node is JsonObject headers && (name.Equals("headers", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("providerHeaders", StringComparison.OrdinalIgnoreCase)))
                    foreach (var key in headers.Select(pair => pair.Key).Where(IsCredentialHeader).ToArray())
                        changed |= headers.Remove(key);
                changed |= RemoveCredentialHeaders(node);
            }
        else if (value is JsonArray array)
            foreach (var node in array) changed |= RemoveCredentialHeaders(node);
        return changed;
    }

    private static bool IsCredentialHeader(string name)
    {
        var key = name.ToLowerInvariant().Replace('_', '-');
        return key is "authorization" or "proxy-authorization" or "cookie" or "set-cookie" or "apikey" or "api-key"
            || key.EndsWith("-key", StringComparison.Ordinal) || key.Contains("token", StringComparison.Ordinal)
            || key.Contains("secret", StringComparison.Ordinal) || key.Contains("password", StringComparison.Ordinal);
    }
}

internal sealed record AgentModelList([property: JsonPropertyName("data")] IReadOnlyList<AgentModelEntry> Data)
{
    [JsonPropertyName("object")]
    public string Object => "list";
}

internal sealed record AgentModelEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long? Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("tags"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Tags,
    [property: JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentDefinition? Agent)
{
    [JsonPropertyName("object")]
    public string Object => "model";
}

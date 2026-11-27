using System.Net.Http.Headers;
using System.Text.Json;
using Streaik.Responses;
using Streaik.Tools;

namespace Streaik.Agents;

internal sealed record AgentHostedTools(HostedToolDefinition[] Client, HostedToolDefinition[] Server, ResponseToolDefinition[] NativeMcp)
{
    internal static AgentHostedTools Read(Agent agent)
    {
        if (agent.AdditionalProperties?.Keys.Any(Agent.IsRemovedField) == true)
            throw new AgentRequestException("Configure MCP and skills in 'tools', not as top-level agent properties.", "invalid_agent_tool");
        List<HostedToolDefinition> client = [], server = [];
        List<ResponseToolDefinition> native = [];
        foreach (var tool in agent.Tools ?? [])
        {
            if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new AgentRequestException("An agent tool must be an object with a string 'type'.", "invalid_agent_tool");
            // Other/native tool types remain portable configuration, not agent-hosted descriptors.
            var kind = type.GetString();
            if (!HostedToolsJson.IsHostedKind(kind)) continue;
            try
            {
                Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
                foreach (var field in tool.EnumerateObject())
                    if (!fields.TryAdd(field.Name, field.Value)) throw new JsonException("Duplicate hosted tool property.");
                var destination = client;
                if (fields.Remove("execution", out var execution))
                {
                    if (kind == "mcp" && execution.ValueKind == JsonValueKind.String && execution.ValueEquals("provider"))
                    {
                        if (fields.ContainsKey("name")) throw new JsonException("A provider MCP descriptor cannot have a hosted name.");
                        fields.Remove("type");
                        native.Add(new() { Type = "mcp", Extra = fields });
                        continue;
                    }
                    if (execution.ValueKind == JsonValueKind.String && execution.ValueEquals("server")) destination = server;
                    else if (execution.ValueKind != JsonValueKind.String || !execution.ValueEquals("client"))
                        throw new JsonException("execution must be 'client' or 'server'.");
                }
                destination.Add(HostedToolsJson.Parse(JsonSerializer.SerializeToElement(fields)));
            }
            catch (JsonException)
            {
                // Framework JSON errors can include user-supplied strings, so only report the descriptor type.
                throw new AgentRequestException($"Invalid '{kind}' tool descriptor. Use 'client' or 'server' for hosted tools, or 'provider' for native MCP.",
                    kind == "skills" ? "invalid_agent_skill" : "invalid_agent_tool");
            }
        }
        return new(client.ToArray(), server.ToArray(), native.ToArray());
    }
}

public sealed partial class AgentRuntime
{
    private const int SkillDownloadLimit = 16 * 1024 * 1024;

    internal static IEnumerable<string> SkillProviders(Agent agent) => AgentHostedTools.Read(agent).Client
        .OfType<SkillsToolDefinition>().Where(tool => tool.Allows(ToolCaller.Direct)).SelectMany(tool => tool.Skills)
        .OfType<SkillReference>().Select(reference => SkillRoute(reference).Provider);

    private static (string Provider, string Path) SkillRoute(SkillReference reference)
    {
        if (reference.SkillId.Split('/') is not [var provider, var id] || provider.Length == 0
            || provider.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
            || string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(c => char.IsControl(c) || c == '\\'))
            throw new AgentRequestException("A referenced skill_id must be in 'provider/skill' form.", "invalid_agent_skill");
        var path = $"v1/skills/{provider}/{Uri.EscapeDataString(id)}";
        if (reference.Version is { } version) path += $"/versions/{Uri.EscapeDataString(version)}";
        return (provider, path + "/content");
    }

    private async ValueTask<Stream> DownloadSkillAsync(string path, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddHeaders(request, headers);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(gateway.Timeout);
        using var response = await gateway.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/zip", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > SkillDownloadLimit)
            throw new InvalidOperationException("The gateway did not return a skill ZIP within the size limit.");

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        var zip = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            for (int read; (read = await body.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0;)
            {
                if (zip.Length + read > SkillDownloadLimit) throw new InvalidOperationException("The gateway skill ZIP exceeds 16 MiB.");
                zip.Write(buffer, 0, read);
            }
            zip.Position = 0;
            return zip;
        }
        catch { zip.Dispose(); throw; }
    }

    private async Task<HostedToolTurn?> OpenToolsAsync(AgentRunTarget target, CancellationToken ct)
    {
        if (target.Tools.Client.Length == 0) return null;
        try { return await HostedToolTurn.CreateAsync(target.Tools.Client, ToolCaller.Direct, new(hostedAccess?.AuthorizeMcp,
            hostedAccess?.ResolveSkill ?? target.ResolveSkill, hostedAccess?.CreateMcpHandler), ct); }
        catch (UnauthorizedAccessException) { throw new AgentRequestException("This agent is not authorized to use the configured MCP tools.", "agent_tool_not_authorized"); }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { throw new AgentRequestException("The agent tools include an invalid skill bundle or hosted configuration.", "invalid_agent_skill"); }
    }
}

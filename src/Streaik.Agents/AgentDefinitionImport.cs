using System.Text.Json;

namespace Streaik.Agents;

internal static class AgentDefinitionImport
{
    public static AgentDefinition? Read(Stream json, DateTimeOffset? lastModified = null)
    {
        var agent = JsonSerializer.Deserialize<AgentDefinition>(json, JsonSerializerOptions.Web);
        if (agent is null || string.IsNullOrWhiteSpace(agent.Name)) return null;
        agent.Created = lastModified?.ToUnixTimeSeconds();
        return agent;
    }
}

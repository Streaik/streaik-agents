using System.Text.Json;

namespace Streaik.Agents;

internal static class AgentDefinitionImport
{
    public static Agent? Read(Stream json, DateTimeOffset? lastModified = null)
    {
        var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web);
        if (agent is null || string.IsNullOrWhiteSpace(agent.Name)) return null;
        agent.Created = lastModified?.ToUnixTimeSeconds();
        return agent;
    }
}

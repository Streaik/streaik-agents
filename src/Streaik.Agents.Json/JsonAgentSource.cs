namespace Streaik.Agents.Json;

public sealed class JsonAgentSource : IAgentSource
{
    private readonly IReadOnlyList<AgentDefinition> _agents = LoadBundledAgents();

    public Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_agents);
    }

    private static IReadOnlyList<AgentDefinition> LoadBundledAgents()
    {
        var assembly = typeof(JsonAgentSource).Assembly;
        var agents = new List<AgentDefinition>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name =>
                     name.StartsWith("Streaik.Agents.Json.Agents.", StringComparison.Ordinal) &&
                     name.EndsWith(".Agent.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            agents.Add(AgentDefinitionImport.Read(stream)
                ?? throw new InvalidOperationException($"Bundled agent '{resource}' must have a name."));
        }

        return agents.AsReadOnly();
    }
}

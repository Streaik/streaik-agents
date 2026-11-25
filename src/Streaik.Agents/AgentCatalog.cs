namespace Streaik.Agents;

public interface IAgentSource
{
    Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentCatalog(IEnumerable<IAgentSource> sources)
{
    private readonly IAgentSource[] _sources = sources.ToArray();

    public async Task<IReadOnlyList<AgentDefinition>> ResolveAsync(IReadOnlyList<string?> names,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (names.Count == 0) return [];
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new AgentRequestException("'model' and 'models' must contain nonempty agent names.");

        var merged = await GetMergedAsync(cancellationToken);
        var resolved = new List<AgentDefinition>(names.Count);
        foreach (var name in names)
        {
            if (!merged.TryGetValue(name!, out var agent))
                throw new AgentRequestException("The named agent was not found.", "agent_not_found");
            AgentRuntime.Validate(agent);
            resolved.Add(agent);
        }
        return resolved;
    }

    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => (await GetMergedAsync(cancellationToken)).Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<AgentDefinition?> FindAsync(string name, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(name) ? null : (await GetMergedAsync(cancellationToken)).GetValueOrDefault(name);

    private async Task<Dictionary<string, AgentDefinition>> GetMergedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var merged = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
        {
            IReadOnlyList<AgentDefinition> agents;
            try
            {
                agents = await source.GetAgentsAsync(cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentCatalogUnavailableException(error);
            }

            // Later registrations may override earlier definitions, as in AIHappey.
            foreach (var agent in agents)
                if (!string.IsNullOrWhiteSpace(agent.Name)) merged[agent.Name] = agent;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return merged;
    }
}

public sealed class AgentCatalogUnavailableException(Exception innerException)
    : Exception("The agent catalog is unavailable.", innerException);

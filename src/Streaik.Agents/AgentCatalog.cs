namespace Streaik.Agents;

public sealed class AgentCatalog(IEnumerable<AgentDefinition> definitions)
{
    private readonly Dictionary<string, AgentDefinition> _agents = definitions.ToDictionary(
        agent => !string.IsNullOrWhiteSpace(agent.Name) ? agent.Name
            : throw new ArgumentException("Every configured agent must have a name."),
        StringComparer.OrdinalIgnoreCase);

    public AgentDefinition Resolve(string? name, AgentDefinition? inline)
    {
        if ((name is null) == (inline is null) || (name is not null && string.IsNullOrWhiteSpace(name)))
            throw new AgentRequestException("Provide exactly one of 'model' (a named agent) or 'agent' (an inline definition).");

        var agent = inline ?? (_agents.TryGetValue(name!, out var configured) ? configured
            : throw new AgentRequestException("The named agent was not found.", "agent_not_found"));
        AgentRuntime.Validate(agent);
        return agent;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Streaik.Agents;

internal sealed record AgentWorkflow(string Type, WorkflowMetadata? Metadata)
{
    internal static readonly string[] SupportedTypes = ["sequential", "concurrent", "handoff", "groupchat", "magentic", "decisions"];

    public AgentDefinition? Router => Metadata?.Decisions is { } decision && Type == "decisions"
        ? new() { Name = "Decisions router", Model = new AgentModel { Id = decision.Model } } : null;

    public static AgentWorkflow Validate(string? type, WorkflowMetadata? metadata, IReadOnlyList<AgentDefinition> agents)
    {
        type ??= "sequential";
        if (!SupportedTypes.Contains(type))
            throw new AgentRequestException("Unknown 'workflowType'. Use sequential, concurrent, handoff, groupchat, magentic, or decisions.");
        if (type != "decisions" && agents.Select(agent => agent.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != agents.Count)
            throw new AgentRequestException("Agent names in a workflow must be unique.");
        if (metadata is not null && (metadata.Decisions is not null && type != "decisions"
            || metadata.Groupchat is not null && type != "groupchat"
            || metadata.Handoff is not null && type != "handoff"
            || metadata.Magentic is not null && type != "magentic"))
            throw new AgentRequestException("'workflowMetadata' must match 'workflowType'.");

        var workflow = new AgentWorkflow(type, metadata);
        switch (type)
        {
            case "handoff":
                _ = workflow.GetHandoffs(agents);
                break;
            case "magentic":
                if (agents.Count < 2)
                    throw new AgentRequestException("Magentic requires a manager followed by at least one participant.");
                if (metadata?.Magentic is { } m && (m.MaxRounds is <= 0 || m.MaxStalls is < 0 || m.MaxResets is < 0))
                    throw new AgentRequestException("Magentic maxRounds must be positive; maxStalls and maxResets cannot be negative.");
                break;
            case "groupchat" when metadata?.Groupchat?.MaximumIterationCount is <= 0:
                throw new AgentRequestException("Group chat maximumIterationCount must be positive.");
            case "decisions":
                _ = AgentRuntime.GetProvider(workflow.Router ?? throw new AgentRequestException("Provide 'workflowMetadata.decisions.model'."));
                break;
        }
        return workflow;
    }

    public IReadOnlyList<(int From, int To)> GetHandoffs(IReadOnlyList<AgentDefinition> agents)
    {
        if (Metadata?.Handoff?.Handoffs is not { Count: > 0 } pairs)
            throw new AgentRequestException("Provide at least one pair in 'workflowMetadata.handoff.handoffs'.");

        var index = agents.Select((agent, i) => (agent.Name!, i)).ToDictionary(pair => pair.Item1, pair => pair.i, StringComparer.OrdinalIgnoreCase);
        var edges = new List<(int From, int To)>();
        var seen = new HashSet<(int From, int To)>();
        foreach (var pair in pairs)
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                || !((pair[0].ValueKind == JsonValueKind.String && pair[1].ValueKind == JsonValueKind.Array)
                    || (pair[1].ValueKind == JsonValueKind.String && pair[0].ValueKind == JsonValueKind.Array)))
                throw new AgentRequestException("Each handoff pair must contain one agent name and one array of agent names.");

            foreach (var from in ResolveSide(pair[0]))
                foreach (var to in ResolveSide(pair[1]))
                {
                    if (from == to || !seen.Add((from, to)))
                        throw new AgentRequestException("Handoffs must be between distinct agents and cannot be repeated.");
                    edges.Add((from, to));
                }
        }
        return edges;

        IEnumerable<int> ResolveSide(JsonElement side)
        {
            if (side.ValueKind == JsonValueKind.String) return [ResolveName(side)];
            if (side.GetArrayLength() == 0) throw new AgentRequestException("A handoff agent list cannot be empty.");
            return side.EnumerateArray().Select(ResolveName).ToArray();
        }

        int ResolveName(JsonElement value) => value.ValueKind == JsonValueKind.String && index.TryGetValue(value.GetString()!, out var i)
            ? i : throw new AgentRequestException("Each handoff must reference a selected agent by name.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowMetadata(DecisionWorkflowOptions? Decisions, GroupChatWorkflowOptions? Groupchat,
    HandoffWorkflowOptions? Handoff, MagenticWorkflowOptions? Magentic);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DecisionWorkflowOptions(string? Model);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GroupChatWorkflowOptions(int MaximumIterationCount = 5);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HandoffWorkflowOptions(IReadOnlyList<JsonElement>? Handoffs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MagenticWorkflowOptions(int? MaxRounds, int? MaxResets, int? MaxStalls, string? ResponseLanguage);

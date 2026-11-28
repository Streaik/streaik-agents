using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Streaik.Tools;

namespace Streaik.Agents;

public sealed partial class AgentRuntime(HttpClient gateway, HostedToolAccess? hostedAccess = null)
{
    public static string GetProvider(Agent agent) => GetProvider(HasId(agent.Model) ? agent.Model : ConfiguredModels(agent).FirstOrDefault());

    internal static string GetProvider(AgentModel? model)
    {
        var id = model?.Id;
        var separator = id?.IndexOf('/') ?? -1;
        if (id is null || separator < 1 || separator == id.Length - 1 || id[separator + 1] == '/'
            || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || id[..separator].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new AgentRequestException("The agent's 'model.id' must be a gateway model ID in 'provider/model' form.");

        return id[..separator].ToLowerInvariant();
    }

    internal static void Validate(Agent agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Name))
            throw new AgentRequestException("The agent's 'name' is required.");
        var models = ConfiguredModels(agent);
        if (models.Length == 0) _ = GetProvider((AgentModel?)null);
        foreach (var model in models) _ = GetProvider(model);
        if (HasId(agent.Model)) _ = GetProvider(agent.Model);
        if (models.All(model => model.Type == "decision") && agent.Model is not { Id.Length: > 0, Type: not "decision" })
            throw new AgentRequestException("An agent needs an answer model; decision models only select an answer model.");
        _ = GetTransport(agent);
        _ = AgentHostedTools.Read(agent);
    }

    private static string GetTransport(Agent agent) => agent.Transport switch
    {
        null or "responses" or Agent.DefaultTransport => Agent.DefaultTransport,
        _ => throw new AgentRequestException("Unsupported agent 'transport'. Only 'v1/responses' is supported.", "unsupported_transport")
    };

    public async Task<AgentResponse> RunAsync(Agent definition, IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? gatewayHeaders = null, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var history = messages.ToList();
        var targets = await ResolveTargetsAsync([definition], history, (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>?>(gatewayHeaders ?? EmptyHeaders), cancellationToken);
        return await RunResolvedAsync(targets[0], history, options, cancellationToken);
    }

    public async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(Agent definition, IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? gatewayHeaders = null, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var history = messages.ToList();
        var targets = await ResolveTargetsAsync([definition], history, (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>?>(gatewayHeaders ?? EmptyHeaders), cancellationToken);
        await foreach (var update in StreamResolvedAsync(targets[0], history, options, cancellationToken)) yield return update;
    }

    internal async Task<AgentResponse> RunResolvedAsync(AgentRunTarget target, IEnumerable<ChatMessage> messages, ChatOptions? options,
        CancellationToken cancellationToken, AgentMcpApprovals? approvals = null, AgentRunAccounting? accounting = null, bool standardProtocol = false)
    {
        await using var turn = await OpenToolsAsync(target, cancellationToken);
        return await CreateAgent(target, turn, accounting: accounting, approvals: approvals, standardProtocol: standardProtocol).RunAsync(messages,
            options: options is null ? null : new ChatClientAgentRunOptions(options), cancellationToken: cancellationToken);
    }

    internal async IAsyncEnumerable<AgentResponseUpdate> StreamResolvedAsync(AgentRunTarget target, IEnumerable<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken, AgentMcpApprovals? approvals = null, bool reportHostedActivity = false,
        AgentRunAccounting? accounting = null, bool standardProtocol = false)
    {
        await using var turn = await OpenToolsAsync(target, cancellationToken);
        await foreach (var update in CreateAgent(target, turn, accounting: accounting, approvals: approvals, reportHostedActivity: reportHostedActivity, standardProtocol: standardProtocol).RunStreamingAsync(messages,
            options: options is null ? null : new ChatClientAgentRunOptions(options), cancellationToken: cancellationToken)) yield return update;
    }

    private ChatClientAgent CreateAgent(AgentRunTarget target, HostedToolTurn? turn, ChatOptions? defaults = null, AgentRunAccounting? accounting = null,
        AgentMcpApprovals? approvals = null, bool reportHostedActivity = false, bool standardProtocol = false)
    {
        var definition = target.Definition;
        IChatClient client = GetTransport(definition) switch
        {
            Agent.DefaultTransport => new ResponsesChatClient(gateway, target.Model, target.Headers, defaults, accounting,
                turn, target.Tools.Server, target.Tools.NativeMcp, approvals, reportHostedActivity, standardProtocol),
            _ => throw new InvalidOperationException("Agent transport was not registered.")
        };
        return new ChatClientAgent(client, instructions: definition.Instructions,
            name: definition.Name, description: definition.Description);
    }
}

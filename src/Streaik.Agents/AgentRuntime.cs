using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Streaik.Agents;

public sealed partial class AgentRuntime(HttpClient gateway)
{
    public static string GetProvider(AgentDefinition agent) => GetProvider(HasId(agent.Model) ? agent.Model : ConfiguredModels(agent).FirstOrDefault());

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

    internal static void Validate(AgentDefinition agent)
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
    }

    private static string GetTransport(AgentDefinition agent) => agent.Transport switch
    {
        null or "responses" or AgentDefinition.DefaultTransport => AgentDefinition.DefaultTransport,
        _ => throw new AgentRequestException("Unsupported agent 'transport'. Only 'v1/responses' is supported.", "unsupported_transport")
    };

    public async Task<AgentResponse> RunAsync(AgentDefinition definition, IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? gatewayHeaders = null, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var history = messages.ToList();
        var targets = await ResolveTargetsAsync([definition], history, (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>?>(gatewayHeaders ?? EmptyHeaders), cancellationToken);
        return await RunResolvedAsync(targets[0], history, options, cancellationToken);
    }

    public async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(AgentDefinition definition, IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? gatewayHeaders = null, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var history = messages.ToList();
        var targets = await ResolveTargetsAsync([definition], history, (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>?>(gatewayHeaders ?? EmptyHeaders), cancellationToken);
        await foreach (var update in StreamResolvedAsync(targets[0], history, options, cancellationToken)) yield return update;
    }

    internal Task<AgentResponse> RunResolvedAsync(AgentRunTarget target, IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        => CreateAgent(target).RunAsync(messages,
            options: options is null ? null : new ChatClientAgentRunOptions(options), cancellationToken: cancellationToken);

    internal IAsyncEnumerable<AgentResponseUpdate> StreamResolvedAsync(AgentRunTarget target, IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        => CreateAgent(target).RunStreamingAsync(messages,
            options: options is null ? null : new ChatClientAgentRunOptions(options), cancellationToken: cancellationToken);

    private ChatClientAgent CreateAgent(AgentRunTarget target, ChatOptions? defaults = null, Action<UsageDetails>? observeUsage = null)
    {
        var definition = target.Definition;
        IChatClient client = GetTransport(definition) switch
        {
            AgentDefinition.DefaultTransport => new ResponsesChatClient(gateway, target.Model, target.Headers, defaults, observeUsage),
            _ => throw new InvalidOperationException("Agent transport was not registered.")
        };
        return new ChatClientAgent(client, instructions: definition.Instructions,
            name: definition.Name, description: definition.Description);
    }
}

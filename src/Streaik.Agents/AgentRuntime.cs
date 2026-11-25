using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Streaik.Agents;

public sealed partial class AgentRuntime(HttpClient gateway)
{
    private const string Responses = "responses";

    public static string GetProvider(AgentDefinition agent)
    {
        var id = agent.Model?.Id;
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
        _ = GetProvider(agent);
        _ = GetTransport(agent);
    }

    private static string GetTransport(AgentDefinition agent) => (agent.Transport ?? Responses) switch
    {
        Responses => Responses,
        _ => throw new AgentRequestException("Unsupported agent 'transport'. Only 'responses' is supported.", "unsupported_transport")
    };

    public Task<AgentResponse> RunAsync(AgentDefinition definition, IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? gatewayHeaders = null, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => CreateAgent(definition, gatewayHeaders).RunAsync(messages,
            options: options is null ? null : new ChatClientAgentRunOptions(options), cancellationToken: cancellationToken);

    public IAsyncEnumerable<AgentResponseUpdate> StreamAsync(AgentDefinition definition, IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? gatewayHeaders = null, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => CreateAgent(definition, gatewayHeaders).RunStreamingAsync(messages,
            options: options is null ? null : new ChatClientAgentRunOptions(options), cancellationToken: cancellationToken);

    private ChatClientAgent CreateAgent(AgentDefinition definition, IReadOnlyDictionary<string, string>? gatewayHeaders,
        ChatOptions? defaults = null, Action<UsageDetails>? observeUsage = null)
    {
        Validate(definition);
        IChatClient client = GetTransport(definition) switch
        {
            Responses => new ResponsesChatClient(gateway, definition.Model!.Id!, gatewayHeaders, defaults, observeUsage),
            _ => throw new InvalidOperationException("Agent transport was not registered.")
        };
        return new ChatClientAgent(client, instructions: definition.Instructions,
            name: definition.Name, description: definition.Description);
    }
}

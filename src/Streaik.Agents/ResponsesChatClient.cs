using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Responses.Extensions;
using Streaik.Responses.Streaming;

namespace Streaik.Agents;

internal sealed class ResponsesChatClient(HttpClient gateway, AgentModel model, IReadOnlyDictionary<string, string>? headers,
    ChatOptions? defaults = null, Action<UsageDetails>? observeUsage = null) : IChatClient
{
    private static readonly HashSet<string> ProtectedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "input", "instructions", "stream", "store", "background", "previous_response_id", "previousResponseId", "conversation",
        "tools", "tool_choice", "toolChoice", "parallel_tool_calls", "multi_agent", "multiAgent", "mcp_servers", "mcpServers", "headers"
    };

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await gateway.GetResponses(CreateRequest(messages, options, stream: false), providerId: null,
            relativeUrl: AgentDefinition.DefaultTransport, headers: headers, ct: cancellationToken);
        CheckResult(result);
        var calls = GetFunctionCalls(result, options).ToList();
        var contents = new List<AIContent>();
        if (GetText(result) is { Length: > 0 } text) contents.Add(new TextContent(text));
        contents.AddRange(calls);
        var usage = GetUsage(result);
        if (usage is not null) observeUsage?.Invoke(usage);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = result.Model,
            ResponseId = result.Id,
            FinishReason = calls.Count == 0 ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls,
            Usage = usage
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var hasText = false;
        await foreach (var part in gateway.GetResponsesUpdates(CreateRequest(messages, options, stream: true),
            providerId: string.Empty, relativeUrl: AgentDefinition.DefaultTransport, headers: headers, ct: cancellationToken))
        {
            switch (part)
            {
                case ResponseOutputTextDelta { Delta.Length: > 0 } delta:
                    hasText = true;
                    yield return new ChatResponseUpdate(ChatRole.Assistant, delta.Delta);
                    break;
                case ResponseCompleted completed:
                    CheckResult(completed.Response);
                    // Some compatible upstreams emit only a final snapshot.
                    if (!hasText && GetText(completed.Response) is { Length: > 0 } text)
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
                    var calls = GetFunctionCalls(completed.Response, options).ToList();
                    var terminal = new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        ModelId = completed.Response.Model,
                        ResponseId = completed.Response.Id,
                        FinishReason = calls.Count == 0 ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls
                    };
                    foreach (var call in calls) terminal.Contents.Add(call);
                    if (GetUsage(completed.Response) is { } usage)
                    {
                        observeUsage?.Invoke(usage);
                        terminal.Contents.Add(new UsageContent(usage));
                    }
                    yield return terminal;
                    yield break;
                case ResponseFailed or ResponseError or ResponseIncompleteEvent:
                    throw new InvalidOperationException("The upstream did not complete the response.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The upstream stream closed without a terminal response.");
    }

    private ResponseRequest CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var request = new ResponseRequest
        {
            Model = model.Id,
            Instructions = options?.Instructions,
            Input = new ResponseInput(messages.SelectMany(MapMessage)),
            Temperature = options?.Temperature ?? defaults?.Temperature ?? model.Options?.Temperature,
            TopP = options?.TopP ?? defaults?.TopP,
            MaxOutputTokens = options?.MaxOutputTokens ?? defaults?.MaxOutputTokens,
            Tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(FunctionTool).ToList(),
            ToolChoice = options?.ToolMode switch
            {
                RequiredChatToolMode { RequiredFunctionName: { } name } => new { type = "function", name },
                RequiredChatToolMode => "required",
                NoneChatToolMode => "none",
                _ => null
            },
            Text = options?.ResponseFormat switch
            {
                ChatResponseFormatJson { Schema: { } schema } json => new
                {
                    format = new { type = "json_schema", name = json.SchemaName ?? "workflow_output", schema, description = json.SchemaDescription }
                },
                ChatResponseFormatJson => new { format = new { type = "json_object" } },
                _ => null
            },
            Stream = stream,
            Store = false
        };
        if (model.ProviderMetadata is not { Count: > 0 } metadata) return request;

        // AIHappey stores native Responses options. Deserialize the merged object so known fields
        // remain typed, while unknown provider options remain extension data. Runtime values win.
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in metadata)
            if (!ProtectedFields.Contains(name)) fields[name] = value;
        foreach (var property in JsonSerializer.SerializeToElement(request, ResponseJson.Default).EnumerateObject())
        {
            fields.Remove(property.Name);
            fields.Add(property.Name, property.Value);
        }
        return JsonSerializer.SerializeToElement(fields).Deserialize<ResponseRequest>(ResponseJson.Default)!;
    }

    private static IEnumerable<ResponseInputItem> MapMessage(ChatMessage message)
    {
        var text = message.Contents.OfType<TextContent>().Select(part => message.Role == ChatRole.Assistant
            ? (ResponseContentPart)new OutputTextPart(part.Text) : new InputTextPart(part.Text)).ToArray();
        if (text.Length != 0)
            yield return new ResponseInputMessage
            {
                Role = message.Role == ChatRole.Assistant ? ResponseRole.Assistant
                    : message.Role == ChatRole.System ? ResponseRole.System : ResponseRole.User,
                Content = new ResponseMessageContent(text)
            };
        foreach (var call in message.Contents.OfType<FunctionCallContent>())
            yield return new ResponseFunctionCallItem { CallId = call.CallId, Name = call.Name, Arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()) };
        foreach (var result in message.Contents.OfType<FunctionResultContent>())
            yield return new ResponseFunctionCallOutputItem { CallId = result.CallId, Output = result.Result as string ?? JsonSerializer.Serialize(result.Result) };
    }

    private static ResponseToolDefinition FunctionTool(AIFunctionDeclaration tool) => new()
    {
        Type = "function",
        Extra = new()
        {
            ["name"] = JsonSerializer.SerializeToElement(tool.Name),
            ["description"] = JsonSerializer.SerializeToElement(tool.Description),
            ["parameters"] = tool.JsonSchema,
            ["strict"] = JsonSerializer.SerializeToElement(false)
        }
    };

    private static IEnumerable<FunctionCallContent> GetFunctionCalls(ResponseResult result, ChatOptions? options)
    {
        foreach (var call in result.OutputItems.OfType<ResponseFunctionCallItem>())
        {
            if (options?.Tools?.OfType<AIFunctionDeclaration>().Any(tool => tool.Name == call.Name) != true
                || string.IsNullOrEmpty(call.CallId))
                throw new InvalidOperationException("The gateway returned an unexpected function call.");
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments)
                ?? throw new JsonException("Function call arguments must be a JSON object.");
            yield return new FunctionCallContent(call.CallId, call.Name, arguments);
        }
    }

    private static void CheckResult(ResponseResult result)
    {
        if (result.Status != "completed" || result.Error is not null)
            throw new InvalidOperationException("The upstream did not complete the response.");
    }

    private static string GetText(ResponseResult result) => result.OutputText ?? string.Concat(result.OutputItems
        .OfType<ResponseInputMessage>().Where(message => message.Role == ResponseRole.Assistant)
        .Select(message => message.Content.Text ?? string.Concat((message.Content.Parts ?? []).OfType<OutputTextPart>().Select(part => part.Text))));

    private static UsageDetails? GetUsage(ResponseResult result) => result.NormalizedUsage is { } usage
        ? new UsageDetails { InputTokenCount = usage.InputTokens, OutputTokenCount = usage.OutputTokens, TotalTokenCount = usage.TotalTokens }
        : null;

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { } // gateway belongs to the host
}

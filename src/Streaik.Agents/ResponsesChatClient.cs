using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Responses.Extensions;
using Streaik.Responses.Streaming;
using Streaik.Tools;

namespace Streaik.Agents;

internal sealed class ResponsesChatClient(HttpClient gateway, AgentModel model, IReadOnlyDictionary<string, string>? headers,
    ChatOptions? defaults = null, Action<UsageDetails>? observeUsage = null, HostedToolTurn? hosted = null,
    HostedToolDefinition[]? gatewayTools = null, ResponseToolDefinition[]? nativeTools = null, AgentMcpApprovals? approvals = null,
    bool reportHostedActivity = false) : IChatClient
{
    private const int MaximumToolRounds = 12;
    private static readonly HashSet<string> ProtectedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "input", "instructions", "stream", "store", "background", "previous_response_id", "previousResponseId", "conversation",
        "tools", "tool_choice", "toolChoice", "parallel_tool_calls", "multi_agent", "multiAgent", "mcp_servers", "mcpServers", "headers"
    };

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var continuation = await BuildInputAsync(messages, cancellationToken);
        var input = continuation.Items;
        ResponseResult result;
        List<AIContent> review;
        UsageDetails? usage = null;
        for (var round = 0; ; round++)
        {
            result = await gateway.GetResponses(CreateRequest(input, options, stream: false, continuation), providerId: null,
                relativeUrl: Agent.DefaultTransport, headers: headers, ct: cancellationToken);
            CheckResult(result);
            AddUsage(result, ref usage);
            continuation.Complete(result, approvals?.Execution, model.Id);
            review = ReadApprovals(result);
            if (review.Count > 0 || !await ContinueHostedAsync(result, input, round, options, cancellationToken)) break;
        }
        var calls = GetFunctionCalls(result, options).ToList();
        var contents = new List<AIContent>();
        if (GetText(result) is { Length: > 0 } text) contents.Add(new TextContent(text));
        contents.AddRange(calls);
        contents.AddRange(review);
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
        var continuation = await BuildInputAsync(messages, cancellationToken);
        var input = continuation.Items;
        UsageDetails? usage = null;
        foreach (var receipt in continuation.Receipts)
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [receipt] };
        continuation.Receipts.Clear();
        var relay = reportHostedActivity && gatewayTools is { Length: > 0 } && options?.ToolMode is not NoneChatToolMode;
        var streamHeaders = headers;
        if (relay)
        {
            var withActivity = new Dictionary<string, string>(headers ?? AgentRuntime.EmptyHeaders, StringComparer.OrdinalIgnoreCase)
                { [HostedToolActivity.ResponsesHeader] = "1" };
            streamHeaders = withActivity;
        }
        for (var round = 0; ; round++)
        {
            var hasText = false;
            ResponseResult? result = null;
            HostedToolActivity? gatewayCall = null;
            await foreach (var part in gateway.GetResponsesUpdates(CreateRequest(input, options, stream: true, continuation),
                providerId: string.Empty, relativeUrl: Agent.DefaultTransport, headers: streamHeaders, ct: cancellationToken))
            {
                if (part is ResponseOutputTextDelta { Delta.Length: > 0 } delta)
                {
                    hasText = true;
                    yield return new ChatResponseUpdate(ChatRole.Assistant, delta.Delta);
                }
                else if (part is ResponseCompleted completed) { result = completed.Response; break; }
                else if (part is ResponseFailed or ResponseError or ResponseIncompleteEvent)
                    throw new InvalidOperationException("The upstream did not complete the response.");
                else if (relay && ReadGatewayActivity(part) is { } activity)
                {
                    if (activity.Output is null && activity.Error is null && gatewayCall is null)
                    {
                        gatewayCall = activity;
                        yield return ActivityUpdate(activity);
                    }
                    else if (gatewayCall is { } pending && pending.CallId == activity.CallId && pending.Name == activity.Name
                        && (activity.Output is not null || activity.Error is not null))
                    {
                        gatewayCall = null;
                        yield return ActivityUpdate(pending with { Output = activity.Output, Error = activity.Error });
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) throw new InvalidOperationException("The upstream stream closed without a terminal response.");
            CheckResult(result);
            AddUsage(result, ref usage);
            if (!hasText && GetText(result) is { Length: > 0 } text) yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            continuation.Complete(result, approvals?.Execution, model.Id);
            foreach (var receipt in continuation.Receipts)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [receipt] };
            continuation.Receipts.Clear();
            var review = ReadApprovals(result);
            var pausedLocalCall = false;
            if (review.Count == 0 && HostedCall(result, round, options) is { } localCall)
            {
                if (reportHostedActivity)
                {
                    await foreach (var content in InvokeWithActivityAsync(result, localCall, input, cancellationToken))
                    {
                        if (content is AgentExecutionApproval.Request) review.Add(content);
                        else yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [content] };
                    }
                }
                else if (await InvokeHostedAsync(result, localCall, input, null, cancellationToken) is { } pending) review.Add(pending);
                if (review.Count == 0) continue;
                pausedLocalCall = true;
            }
            // The paused call belongs to this host, not to the browser function dispatcher.
            var calls = pausedLocalCall ? [] : GetFunctionCalls(result, options).ToList();
            var terminal = new ChatResponseUpdate
            {
                Role = ChatRole.Assistant, ModelId = result.Model, ResponseId = result.Id,
                FinishReason = calls.Count == 0 && review.Count == 0 ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls
            };
            foreach (var call in calls) terminal.Contents.Add(call);
            foreach (var item in review) terminal.Contents.Add(item);
            if (usage is not null) terminal.Contents.Add(new UsageContent(usage));
            yield return terminal;
            yield break;
        }
    }

    private List<AIContent> ReadApprovals(ResponseResult result)
    {
        if (!result.OutputItems.OfType<ResponseMcpApprovalRequestItem>().Any()) return [];
        if (hosted is not null && result.OutputItems.OfType<ResponseFunctionCallItem>().Any(call => hosted.Owns(call.Name)))
            throw new InvalidOperationException("The upstream mixed an approval with a backend tool call.");
        return approvals?.Observe(model.Id, result) ?? throw new AgentRequestException(
            "The upstream requested MCP approval on a route that cannot continue it.", "mcp_approval_required");
    }

    private async Task<bool> ContinueHostedAsync(ResponseResult result, List<ResponseInputItem> input, int round, ChatOptions? options, CancellationToken ct)
    {
        if (HostedCall(result, round, options) is not { } call) return false;
        await InvokeHostedAsync(result, call, input, null, ct);
        return true;
    }

    private ResponseFunctionCallItem? HostedCall(ResponseResult result, int round, ChatOptions? options)
    {
        if (hosted is null) return null;
        var calls = result.OutputItems.OfType<ResponseFunctionCallItem>().ToArray();
        if (!calls.Any(call => hosted.Owns(call.Name))) return null;
        if (round >= MaximumToolRounds || calls.Length != 1 || calls[0].Namespace is not null || string.IsNullOrEmpty(calls[0].CallId)
            || options?.ToolMode is NoneChatToolMode
            || options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: { } name } && calls[0].Name != name)
            throw new InvalidOperationException("The gateway returned an unexpected hosted tool call.");
        return calls[0];
    }

    private async Task<AgentExecutionApproval.Request?> InvokeHostedAsync(ResponseResult result, ResponseFunctionCallItem call, List<ResponseInputItem> input,
        Func<HostedToolActivity, Task>? observe, CancellationToken ct)
    {
        using var arguments = JsonDocument.Parse(call.Arguments);
        var outcome = observe is null
            ? await hosted!.InvokeAsync(call.Name, call.CallId, arguments.RootElement, ct)
            : await hosted!.InvokeAsync(call.Name, call.CallId, arguments.RootElement, observe, ct);
        if (outcome is McpCallOutcome.ApprovalRequired { Approval: var approval } && approvals?.Execution is { } execution)
            return execution.Issue(model.Id, new("local", approval.ServerLabel, approval.ToolName, approval.CallId), approval.Input);
        if (outcome is not McpCallOutcome.Completed completed)
            throw new AgentRequestException("This MCP call requires approval before execution and was not dispatched.", "mcp_approval_required");
        if (GetText(result) is { Length: > 0 } text)
            input.Add(new ResponseInputMessage { Role = ResponseRole.Assistant, Content = new ResponseMessageContent([new OutputTextPart(text)]) });
        input.Add(new ResponseFunctionCallItem { CallId = call.CallId, Name = call.Name, Arguments = call.Arguments });
        input.Add(new ResponseFunctionCallOutputItem { CallId = call.CallId, Output = completed.Output.GetRawText() });
        if (input.RemoveAll(item => item is ResponseMcpApprovalResponseItem) > 0)
            input.AddRange(result.OutputItems.OfType<ResponseMcpCallItem>());
        return null;
    }

    private async IAsyncEnumerable<AIContent> InvokeWithActivityAsync(ResponseResult result, ResponseFunctionCallItem call,
        List<ResponseInputItem> input, [EnumeratorCancellation] CancellationToken ct)
    {
        // One invocation, at most two frames. Cancel and join it if enumeration stops early.
        var queue = Channel.CreateUnbounded<AIContent>(new() { SingleReader = true, SingleWriter = true });
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task ExecuteAsync()
        {
            try
            {
                var pending = await InvokeHostedAsync(result, call, input,
                    activity => queue.Writer.WriteAsync(new HostedToolActivityContent(activity with { Input = activity.Input.Clone() }), invocation.Token).AsTask(), invocation.Token);
                if (pending is not null) await queue.Writer.WriteAsync(pending, invocation.Token);
                queue.Writer.Complete();
            }
            catch (Exception error) { queue.Writer.TryComplete(error); }
        }
        var execution = ExecuteAsync();
        try
        {
            await foreach (var activity in queue.Reader.ReadAllAsync(ct)) yield return activity;
        }
        finally
        {
            await invocation.CancelAsync();
            await execution;
        }
    }

    private static ChatResponseUpdate ActivityUpdate(HostedToolActivity activity) => new()
    {
        Role = ChatRole.Assistant, Contents = [new HostedToolActivityContent(activity)]
    };

    private static HostedToolActivity? ReadGatewayActivity(ResponseStreamPart part)
    {
        if (part is not ResponseUnknownEvent { Type: HostedToolActivity.ResponsesEvent } evt
            || evt.Data?.GetValueOrDefault("activity") is not { ValueKind: JsonValueKind.Object } data) return null;
        var activity = data.Deserialize<HostedToolActivity>(JsonSerializerOptions.Web);
        return activity is { CallId.Length: > 0, Name.Length: > 0, Input.ValueKind: not JsonValueKind.Undefined } ? activity : null;
    }

    private void AddUsage(ResponseResult result, ref UsageDetails? total)
    {
        if (GetUsage(result) is not { } usage) return;
        observeUsage?.Invoke(usage);
        (total ??= new UsageDetails()).Add(usage);
    }

    private sealed class Continuation
    {
        public List<ResponseInputItem> Items { get; } = [];
        public Dictionary<string, string> Proofs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> History { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (string Id, JsonElement Input, AgentExecutionApproval.Receipt Receipt)> Forwarded { get; } = new(StringComparer.Ordinal);
        public List<AgentExecutionApproval.Request> Receipts { get; } = [];

        public void Complete(ResponseResult response, AgentExecutionApproval? execution, string? model)
        {
            if (Forwarded.Count == 0) return;
            Items.RemoveAll(item => item is ResponseMcpApprovalRequestItem or ResponseMcpApprovalResponseItem);
            foreach (var item in response.OutputItems.OfType<ResponseMcpCallItem>())
            {
                if (item.ApprovalRequestId is not { } approvalId || !Forwarded.Remove(approvalId, out var result)) continue;
                var proof = item.Id is { } id ? AgentExecutionApproval.Proof(response, AgentExecutionApproval.HistoryKey, id) : null;
                var output = JsonSerializer.SerializeToElement(item, ResponseJson.Default);
                if (execution is not null && output.GetRawText().Length <= 512 * 1024)
                    Receipts.Add(execution.Complete(model, result.Id, result.Input, result.Receipt with { HistoryProof = proof }, output));
                Items.Add(item);
                if (item.Id is { } itemId && proof is not null) History[itemId] = proof;
            }
            Forwarded.Clear();
            Proofs.Clear();
        }
    }

    private async Task<Continuation> BuildInputAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var continuation = new Continuation();
        var history = messages.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var completed = history.SelectMany(message => message.Contents.OfType<AgentExecutionApproval.Decision>())
            .Where(decision => Streaik.Vercel.Security.HostedToolContinuation.IsResult(decision.Part)).Select(decision => decision.Part.ToolCallId).ToHashSet(StringComparer.Ordinal);
        foreach (var message in history)
        {
            continuation.Items.AddRange(MapMessage(message));
            foreach (var decision in message.Contents.OfType<AgentExecutionApproval.Decision>())
            {
                if (approvals?.Execution is not { } execution) throw new InvalidOperationException("Missing MCP continuation handler.");
                var id = decision.Part.ToolCallId;
                if (!seen.Add(id)) throw AgentExecutionApproval.Invalid();
                if (Streaik.Vercel.Security.HostedToolContinuation.IsResult(decision.Part))
                {
                    var (receipt, input, output) = execution.ReadResult(model.Id, decision);
                    if (receipt.Call.Kind == "native")
                    {
                        var item = output.Deserialize<ResponseMcpCallItem>(ResponseJson.Default);
                        if (item?.ApprovalRequestId != receipt.Call.CallId || item.ServerLabel != receipt.Call.Server || item.Name != receipt.Call.Name)
                            throw AgentExecutionApproval.Invalid();
                        continuation.Items.Add(item);
                        if (item.Id is { } nativeId && receipt.HistoryProof is { } proof) continuation.History[nativeId] = proof;
                    }
                    else AddLocal(continuation.Items, receipt.Call, input, receipt.Approved ? output : DeniedMcp);
                    continue;
                }
                if (completed.Contains($"run_{id}")) continue;
                var (pending, args, approved, descriptor) = execution.ReadDecision(model.Id, decision);
                var completion = new AgentExecutionApproval.Receipt(pending, approved);
                if (pending.Kind == "native")
                {
                    var arguments = pending.Arguments ?? "{}";
                    if (arguments.Length > 65536 || !JsonElement.DeepEquals(args, JsonSerializer.Deserialize<JsonElement>(arguments))) throw AgentExecutionApproval.Invalid();
                    if (!continuation.Forwarded.TryAdd(pending.CallId, (id, args, completion))) throw AgentExecutionApproval.Invalid();
                    if (pending.Proof is { } token) continuation.Proofs.Add(pending.CallId, token);
                    continuation.Items.Add(new ResponseMcpApprovalRequestItem { Id = pending.CallId, ServerLabel = pending.Server, Name = pending.Name, Arguments = arguments });
                    continuation.Items.Add(new ResponseMcpApprovalResponseItem { ApprovalRequestId = pending.CallId, Approve = approved });
                    continue;
                }
                var value = DeniedMcp;
                if (approved)
                {
                    if (hosted is null || await hosted.ResumeApprovedAsync(new(id, pending.CallId, pending.Server,
                        McpToolSession.EndpointBinding(descriptor.ServerUrl), pending.Name, ToolCaller.Direct, args), ct) is not McpCallOutcome.Completed done)
                        throw AgentExecutionApproval.Invalid();
                    value = done.Output;
                    if (value.GetRawText().Length > 512 * 1024) throw new AgentRequestException("The MCP result is too large.", "mcp_approval_invalid");
                }
                AddLocal(continuation.Items, pending, args, value);
                continuation.Receipts.Add(execution.Complete(model.Id, id, args, completion, value));
            }
        }
        return continuation;
    }

    private static readonly JsonElement DeniedMcp = JsonSerializer.SerializeToElement("The user declined to run the MCP tool.");
    private static void AddLocal(List<ResponseInputItem> items, AgentExecutionApproval.Call call, JsonElement input, JsonElement output)
    {
        items.Add(new ResponseFunctionCallItem { CallId = call.CallId, Name = HostedToolTurn.McpFunctionName("", call.Server, call.Name), Arguments = input.GetRawText() });
        items.Add(new ResponseFunctionCallOutputItem { CallId = call.CallId, Output = output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText() });
    }

    private ResponseRequest CreateRequest(IEnumerable<ResponseInputItem> input, ChatOptions? options, bool stream, Continuation continuation)
    {
        var enableTools = options?.ToolMode is not NoneChatToolMode;
        var functions = options?.Tools?.OfType<AIFunctionDeclaration>().Select(FunctionTool).ToList();
        if (enableTools && hosted is not null)
        {
            foreach (var tool in hosted.Functions())
            {
                if (options?.Tools?.OfType<AIFunctionDeclaration>().Any(f => f.Name == tool.Name) == true)
                    throw new AgentRequestException("A function name collides with an agent-hosted tool.", "invalid_agent_tool");
                (functions ??= []).Add(FunctionTool(tool.Name, tool.Description, tool.InputSchema));
            }
        }
        if (enableTools && gatewayTools is { Length: > 0 })
        {
            functions ??= [];
            foreach (var tool in gatewayTools)
            {
                var descriptor = JsonSerializer.SerializeToElement<HostedToolDefinition>(tool, HostedToolsJson.Options);
                var type = descriptor.GetProperty("type").GetString()!;
                functions.Add(new ResponseToolDefinition { Type = type, Extra = descriptor.EnumerateObject()
                    .Where(field => field.Name != "type").ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal) });
                functions[^1].Extra!["name"] = JsonSerializer.SerializeToElement(type);
            }
        }
        if (enableTools && nativeTools is { Length: > 0 }) (functions ??= []).AddRange(nativeTools);
        var request = new ResponseRequest
        {
            Model = model.Id,
            Instructions = !enableTools || hosted?.Instructions is not { } catalog ? options?.Instructions : string.IsNullOrWhiteSpace(options?.Instructions)
                ? catalog : $"{options.Instructions}\n\n{catalog}",
            Input = new ResponseInput(input),
            Temperature = options?.Temperature ?? defaults?.Temperature ?? model.Options?.Temperature,
            TopP = options?.TopP ?? defaults?.TopP,
            MaxOutputTokens = options?.MaxOutputTokens ?? defaults?.MaxOutputTokens,
            Tools = functions,
            ParallelToolCalls = enableTools && (hosted is not null || gatewayTools is { Length: > 0 }) ? false : null,
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
        if (continuation.Proofs.Count != 0)
            (request.AdditionalProperties ??= [])[AgentExecutionApproval.ProofsKey] = JsonSerializer.SerializeToElement(continuation.Proofs, ResponseJson.Default);
        if (continuation.History.Count != 0)
            (request.AdditionalProperties ??= [])[AgentExecutionApproval.HistoryKey] = JsonSerializer.SerializeToElement(continuation.History, ResponseJson.Default);
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
            yield return new ResponseFunctionCallOutputItem { CallId = result.CallId, Output = result.Result switch
            {
                string plain => plain,
                JsonElement { ValueKind: JsonValueKind.String } value => value.GetString()!,
                _ => JsonSerializer.Serialize(result.Result)
            } };
        foreach (var native in message.Contents.OfType<NativeResponseContent>()) yield return native.Item;
    }

    private static ResponseToolDefinition FunctionTool(AIFunctionDeclaration tool) => FunctionTool(tool.Name, tool.Description, tool.JsonSchema);

    private static ResponseToolDefinition FunctionTool(string name, string? description, JsonElement schema) => new()
    {
        Type = "function",
        Extra = new()
        {
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["description"] = JsonSerializer.SerializeToElement(description),
            ["parameters"] = schema,
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
            yield return options!.Tools!.OfType<BrowserFunction>().Any(tool => tool.Name == call.Name)
                ? new BrowserFunctionCall(call.CallId, call.Name, arguments) : new FunctionCallContent(call.CallId, call.Name, arguments);
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

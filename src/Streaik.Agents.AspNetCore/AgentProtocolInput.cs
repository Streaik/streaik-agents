using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Streaik.Agents.AspNetCore;

internal static class AgentProtocolInput
{
    private static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
    private static readonly JsonSerializerOptions ChatJson = new() { PropertyNameCaseInsensitive = true };

    public static async Task<AgentInput> ParseAsync(JsonElement body, AgentCatalog catalog, AgentEndpointProtocol protocol, CancellationToken ct)
    {
        var chat = protocol == AgentEndpointProtocol.ChatCompletions;
        var fields = chat
            ? new[] { "model", "messages", "stream", "stream_options", "max_tokens", "max_completion_tokens", "temperature", "top_p",
                "n", "store", "tools", "tool_choice", "parallel_tool_calls" }
            : ["model", "messages", "system", "stream", "max_tokens", "temperature", "top_p", "tools", "tool_choice"];
        if (body.ValueKind != JsonValueKind.Object) throw new AgentRequestException("The request body must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in body.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new AgentRequestException("The request has duplicate fields.");
            if (property.Value.ValueKind != JsonValueKind.Null && !fields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw new AgentRequestException("The request includes a field not supported by this agent endpoint.");
        }

        var request = chat ? FromChat(body.Deserialize<ChatCompletionOptions>(ChatJson)!) : FromMessages(body.Deserialize<MessagesRequest>(MessagesJson.Default)!);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new AgentRequestException("'model' must name a catalog agent.");
        if (!request.History.Any(message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text)))
            throw new AgentRequestException("Provide at least one user text message.");
        var agents = await catalog.ResolveAsync([request.Model], ct);
        return new(agents, AgentWorkflow.Validate(null, null, agents, "workflow_type", "workflow_metadata"),
            request.History, request.Stream, request.Options, IncludeUsage: request.IncludeUsage);
    }

    private static ProtocolRequest FromChat(ChatCompletionOptions request)
    {
        if (request.NumberOfChoices is not (null or 1)) throw new AgentRequestException("Only one choice is supported ('n': 1).");
        if (request.Store == true) throw new AgentRequestException("Agent runs are stateless; 'store' must be false.");
        if (request.StreamOptions is not null && request.Stream != true) throw new AgentRequestException("'stream_options' requires 'stream': true.");
        if (request.MaxTokens is <= 0 || request.MaxCompletionTokens is <= 0)
            throw new AgentRequestException("Token limits must be positive.");
        var options = Options(request.MaxCompletionTokens ?? request.MaxTokens, request.Temperature, request.TopP, request.ParallelToolCalls);
        var tools = (request.Tools ?? []).Select(tool => tool is ChatCompletionTool { Function: { Strict: not true } function }
            ? Declaration(function.Name, function.Description, function.Parameters ?? EmptySchema)
            : throw new AgentRequestException("Only non-strict function tools are supported.")).ToList();
        ChatToolMode mode = request.ToolChoiceOption switch
        {
            null or ChatCompletionToolChoiceMode { Mode: "auto" } => ChatToolMode.Auto,
            ChatCompletionToolChoiceMode { Mode: "none" } => ChatToolMode.None,
            ChatCompletionToolChoiceMode { Mode: "required" } => ChatToolMode.RequireAny,
            ChatCompletionNamedToolChoice { Function.Name: { Length: > 0 } name } => ChatToolMode.RequireSpecific(name),
            _ => throw new AgentRequestException("Unsupported 'tool_choice'.")
        };
        SetTools(options, tools, mode);

        var history = new ToolHistory();
        var messages = new List<ChatMessage>();
        foreach (var message in request.Messages ?? [])
        {
            if (message is null || message.Audio is not null || message.Refusal is not null || message.FunctionCall is not null)
                throw new AgentRequestException("Only text and modern function-tool history are supported.");
            var role = message.Role switch
            {
                "system" or "developer" => ChatRole.System,
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                _ => throw new AgentRequestException("Unsupported Chat Completions message role.")
            };
            List<AIContent> parts = message.Content.Text is { } text ? [new TextContent(text)] : (message.Content.Parts ?? []).Select<ChatMessageContentPart, AIContent>(part =>
                part is ChatMessageTextPart { Text: { } value } ? new TextContent(value) : throw new AgentRequestException("Only text content parts are supported.")).ToList();
            if (role == ChatRole.Tool)
            {
                if (parts.Count == 0 || message.ToolCalls?.Any() == true) throw new AgentRequestException("A tool message requires text content and a tool call ID.");
                messages.Add(new(ChatRole.Tool, [history.Result(message.ToolCallId, string.Concat(parts.OfType<TextContent>().Select(part => part.Text)))]));
                continue;
            }
            history.RequireResults();
            if (message.ToolCallId is not null) throw new AgentRequestException("Only tool messages can specify 'tool_call_id'.");
            foreach (var tool in message.ToolCalls ?? [])
            {
                if (role != ChatRole.Assistant || tool is not ChatCompletionMessageToolCall { Function.Arguments: { } args } call)
                    throw new AgentRequestException("Only assistant messages can include function calls.");
                parts.Add(history.Call(call.Id, call.Function.Name, JsonSerializer.Deserialize<JsonElement>(args)));
            }
            AddMessage(messages, role, parts);
        }
        history.RequireResults();
        return new(request.Model, messages, request.Stream == true, options, request.StreamOptions?.IncludeUsage == true);
    }

    private static ProtocolRequest FromMessages(MessagesRequest request)
    {
        if (request.MaxTokens <= 0) throw new AgentRequestException("'max_tokens' must be positive.");
        var choice = request.ToolChoice;
        var options = Options(request.MaxTokens, request.Temperature, request.TopP,
            choice?.DisableParallelToolUse is { } disabled ? !disabled : null);
        var tools = (request.Tools ?? []).Select(tool => tool is { Type: null or "custom", InputSchema: { } schema }
            ? Declaration(tool.Name, tool.Description, schema)
            : throw new AgentRequestException("Only custom function tools with an 'input_schema' are supported.")).ToList();
        ChatToolMode mode = choice switch
        {
            null or { Type: "auto" } => ChatToolMode.Auto,
            { Type: "none" } => ChatToolMode.None,
            { Type: "any" } => ChatToolMode.RequireAny,
            { Type: "tool", Name: { Length: > 0 } name } => ChatToolMode.RequireSpecific(name),
            _ => throw new AgentRequestException("Unsupported 'tool_choice'.")
        };
        SetTools(options, tools, mode);

        var messages = new List<ChatMessage>();
        if (request.System is { } system) AddMessage(messages, ChatRole.System, TextOnly(system).Select(text => (AIContent)new TextContent(text)).ToList());
        var history = new ToolHistory();
        foreach (var message in request.Messages ?? [])
        {
            if (message?.Content is null || message.Role is not ("user" or "assistant"))
                throw new AgentRequestException("Messages must use the 'user' or 'assistant' role and include content.");
            var assistant = message.Role == "assistant";
            if (assistant) history.RequireResults();
            var blocks = message.Content.Text is { } text ? [new MessageContentBlock { Type = "text", Text = text }]
                : message.Content.Blocks ?? throw new AgentRequestException("Expected text or an array of content blocks.");
            List<AIContent> parts = [];
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case { Type: "text", Text: { } value }:
                        if (!assistant) history.RequireResults();
                        parts.Add(new TextContent(value));
                        break;
                    case { Type: "tool_use", Input: { } input } when assistant:
                        AddMessage(messages, ChatRole.Assistant, parts);
                        parts = [];
                        messages.Add(new(ChatRole.Assistant, [history.Call(block.Id, block.Name, input)]));
                        break;
                    case { Type: "tool_result" } when !assistant && parts.Count == 0:
                        var output = block.Content is { } content ? string.Concat(TextOnly(content)) : string.Empty;
                        messages.Add(new(ChatRole.Tool, [history.Result(block.ToolUseId, block.IsError == true
                            ? $"Tool execution failed: {output}" : output)]));
                        break;
                    default:
                        throw new AgentRequestException("Only text, assistant tool_use, and leading user tool_result blocks are supported.");
                }
            }
            AddMessage(messages, assistant ? ChatRole.Assistant : ChatRole.User, parts);
            if (!assistant) history.RequireResults();
        }
        history.RequireResults();
        return new(request.Model, messages, request.Stream == true, options);
    }

    private static IEnumerable<string> TextOnly(MessagesContent content) => content.Text is { } text ? [text] :
        (content.Blocks ?? throw new AgentRequestException("Expected text content.")).Select(block => block is { Type: "text", Text: { } value }
            ? value : throw new AgentRequestException("Only text blocks are supported here."));

    private static void AddMessage(List<ChatMessage> messages, ChatRole role, List<AIContent> parts)
    {
        if (parts.Count > 0) messages.Add(new(role, parts));
    }

    private static ChatOptions Options(int? tokens, double? temperature, double? topP, bool? parallel)
    {
        if (temperature is { } t && (!double.IsFinite(t) || t is < 0 or > 2) || topP is { } p && (!double.IsFinite(p) || p is < 0 or > 1))
            throw new AgentRequestException("'temperature' must be between 0 and 2 and 'top_p' between 0 and 1.");
        return new() { MaxOutputTokens = tokens, Temperature = (float?)temperature, TopP = (float?)topP, AllowMultipleToolCalls = parallel };
    }

    private static AITool Declaration(string? name, string? description, JsonElement schema)
    {
        if (!IsFunctionName(name) || schema.ValueKind != JsonValueKind.Object)
            throw new AgentRequestException("Tools must have a valid function name and a JSON Schema object.");
        return new CallerFunction(name!, description, schema);
    }

    private static bool IsFunctionName(string? name) => name is { Length: > 0 and <= 64 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
        && !name.StartsWith("streaik_", StringComparison.Ordinal); // Reserved by the gateway for its hosted tools.

    private static void SetTools(ChatOptions options, List<AITool> tools, ChatToolMode mode)
    {
        if (tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new AgentRequestException("Function tool names must be unique.");
        if (mode is RequiredChatToolMode required && (tools.Count == 0 || required.RequiredFunctionName is { } name && !tools.Any(tool => tool.Name == name)))
            throw new AgentRequestException("Required tool choices must reference declared function tools.");
        options.Tools = tools.Count > 0 ? tools : null;
        options.ToolMode = mode;
    }

    private sealed class CallerFunction(string name, string? description, JsonElement schema) : AIFunctionDeclaration
    {
        public override string Name => name;
        public override string Description => description ?? string.Empty;
        public override JsonElement JsonSchema => schema;
    }

    private sealed class ToolHistory
    {
        private readonly HashSet<string> _calls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        public FunctionCallContent Call(string? id, string? name, JsonElement input)
        {
            if (string.IsNullOrWhiteSpace(id) || !IsFunctionName(name) || input.ValueKind != JsonValueKind.Object || !_calls.Add(id))
                throw new AgentRequestException("A function call requires a unique ID, a valid name, and JSON object arguments.");
            _pending.Add(id);
            return new(id, name!, input.Deserialize<Dictionary<string, object?>>()!);
        }

        public FunctionResultContent Result(string? id, string output) => id is not null && _pending.Remove(id)
            ? new(id, output) : throw new AgentRequestException("Each tool result must match a pending function call ID exactly once.");

        public void RequireResults()
        {
            if (_pending.Count > 0) throw new AgentRequestException("Provide a result for each assistant function call before continuing.");
        }
    }

    private sealed record ProtocolRequest(string Model, List<ChatMessage> History, bool Stream, ChatOptions Options, bool IncludeUsage = false);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;

namespace Streaik.Agents.AspNetCore;

internal sealed class AgentProtocolOutput(HttpResponse response, AgentEndpointProtocol protocol, string model,
    CancellationToken ct, AgentRunAccounting accounting, TimeProvider clock, bool includeUsage)
{
    private static readonly JsonElement Null = JsonSerializer.SerializeToElement<string?>(null);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonSerializerOptions OmitNull = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _id = (protocol == AgentEndpointProtocol.ChatCompletions ? "chatcmpl-" : "msg_") + Guid.NewGuid().ToString("N");
    private readonly long _created = clock.GetUtcNow().ToUnixTimeSeconds();
    private bool IsChat => protocol == AgentEndpointProtocol.ChatCompletions;
    private int _blockIndex;
    private int _toolIndex;
    private bool _textBlockOpen;
    private ChatFinishReason? _finishReason;

    public Task WriteResultAsync(AgentResponse result)
    {
        var calls = result.Messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
        var finish = (result.RawRepresentation as ChatResponse)?.FinishReason;
        if (IsChat)
            return response.WriteAsJsonAsync(new ChatCompletion
            {
                Id = _id, Created = _created, Model = model, Usage = ChatUsage(),
                Choices = [new() { FinishReason = Finish(finish, calls.Length > 0), Message = new()
                {
                    Content = result.Text.Length > 0 || calls.Length == 0 ? result.Text : null,
                    ToolCalls = calls.Length == 0 ? null : calls.Select(call => new ChatCompletionMessageToolCall
                        { Id = call.CallId, Function = new() { Name = call.Name, Arguments = Arguments(call).GetRawText() } }),
                    AdditionalProperties = result.Text.Length == 0 && calls.Length > 0 ? new() { ["content"] = Null } : null
                } }]
            }, ct);

        var message = Message(MessagesUsage());
        if (result.Text.Length > 0) message.Content.Add(new() { Type = "text", Text = result.Text });
        message.Content.AddRange(calls.Select(call => ToolUse(call, Arguments(call))));
        message.StopReason = Finish(finish, calls.Length > 0);
        message.AdditionalProperties = new() { ["stop_sequence"] = Null };
        return response.WriteAsJsonAsync(message, MessagesJson.Default, ct);
    }

    public Task StartAsync()
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        if (IsChat) return ChunkAsync(new() { Role = "assistant" });
        var start = Message(new() { InputTokens = 0, OutputTokens = 0 });
        start.AdditionalProperties = new() { ["stop_reason"] = Null, ["stop_sequence"] = Null };
        return EventAsync(new() { Type = "message_start", Message = start });
    }

    public async Task WriteAsync(AgentResponseUpdate update)
    {
        if (update.RawRepresentation is ChatResponseUpdate { FinishReason: { } finish }) _finishReason = finish;
        foreach (var content in update.Contents)
        {
            if (content is TextContent { Text.Length: > 0 } text)
            {
                if (IsChat) await ChunkAsync(new() { Content = text.Text });
                else
                {
                    if (!_textBlockOpen)
                    {
                        await EventAsync(new() { Type = "content_block_start", Index = _blockIndex, ContentBlock = new() { Type = "text", Text = "" } });
                        _textBlockOpen = true;
                    }
                    await EventAsync(new() { Type = "content_block_delta", Index = _blockIndex, Delta = new() { Type = "text_delta", Text = text.Text } });
                }
            }
            else if (content is FunctionCallContent call)
            {
                var arguments = Arguments(call).GetRawText();
                if (IsChat) await ChunkAsync(new() { ToolCalls = [new ChatCompletionToolCallChunk
                {
                    Index = _toolIndex, Id = call.CallId, Type = "function", Function = new() { Name = call.Name, Arguments = arguments }
                }] });
                else
                {
                    await CloseTextAsync();
                    await EventAsync(new() { Type = "content_block_start", Index = _blockIndex, ContentBlock = ToolUse(call, EmptyObject) });
                    await EventAsync(new() { Type = "content_block_delta", Index = _blockIndex, Delta = new() { Type = "input_json_delta", PartialJson = arguments } });
                    await EventAsync(new() { Type = "content_block_stop", Index = _blockIndex++ });
                }
                _toolIndex++;
            }
        }
    }

    public async Task CompleteAsync()
    {
        var finish = Finish(_finishReason, _toolIndex > 0);
        if (IsChat)
        {
            await ChunkAsync(new(), finish);
            if (includeUsage) await WriteChunkAsync([], ChatUsage());
            await FrameAsync("[DONE]");
        }
        else
        {
            await CloseTextAsync();
            await EventAsync(new() { Type = "message_delta", Usage = MessagesUsage(), Delta = new()
            {
                StopReason = finish, AdditionalProperties = new() { ["stop_sequence"] = Null }
            } });
            await EventAsync(new() { Type = "message_stop" });
        }
    }

    public async Task FailAsync(int status, string code, string message)
    {
        await FrameAsync(JsonSerializer.Serialize(Error(protocol, status, code, message)), IsChat ? null : "error");
        if (IsChat) await FrameAsync("[DONE]");
    }

    public static Task ErrorAsync(HttpContext context, AgentEndpointProtocol protocol, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Error(protocol, status, code, message), context.RequestAborted);
    }

    private static object Error(AgentEndpointProtocol protocol, int status, string code, string message)
    {
        var type = status switch
        {
            401 => "authentication_error", 403 => "permission_error", 404 => "not_found_error", 429 => "rate_limit_error",
            >= 500 => "api_error", _ => "invalid_request_error"
        };
        return protocol == AgentEndpointProtocol.Messages ? new { type = "error", error = new { type, message } }
            : new { error = new { type, code, message, param = (string?)null } };
    }

    private string Finish(ChatFinishReason? reason, bool calls) => reason == ChatFinishReason.Length ? (IsChat ? "length" : "max_tokens")
        : calls ? (IsChat ? "tool_calls" : "tool_use") : IsChat ? "stop" : "end_turn";

    private static JsonElement Arguments(FunctionCallContent call) => JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>());

    private static MessageContentBlock ToolUse(FunctionCallContent call, JsonElement input) => new()
        { Type = "tool_use", Name = call.Name, Id = call.CallId, Input = input };

    private MessagesResponse Message(MessagesUsage usage) => new() { Type = "message", Id = _id, Role = "assistant", Model = model, Usage = usage };

    private MessagesUsage MessagesUsage()
    {
        var (usage, _) = accounting.Snapshot();
        return new() { InputTokens = usage?.InputTokens ?? 0, OutputTokens = usage?.OutputTokens ?? 0 };
    }

    private ChatCompletionUsage? ChatUsage()
    {
        var (usage, _) = accounting.Snapshot();
        if (usage is null) return null;
        var input = usage.InputTokens ?? 0;
        var output = usage.OutputTokens ?? 0;
        return new()
        {
            PromptTokens = input, CompletionTokens = output, TotalTokens = usage.TotalTokens ?? (int)Math.Min((long)input + output, int.MaxValue),
            PromptTokensDetails = usage.InputTokensDetails?.CachedTokens is { } cached ? new() { CachedTokens = cached } : null,
            CompletionTokensDetails = usage.OutputTokensDetails?.ReasoningTokens is { } reasoning ? new() { ReasoningTokens = reasoning } : null
        };
    }

    private async Task CloseTextAsync()
    {
        if (!_textBlockOpen) return;
        _textBlockOpen = false;
        await EventAsync(new() { Type = "content_block_stop", Index = _blockIndex++ });
    }

    private Task ChunkAsync(ChatCompletionDelta delta, string? finish = null) => WriteChunkAsync([new()
        { Delta = delta, FinishReason = finish, AdditionalProperties = finish is null ? new() { ["finish_reason"] = Null } : null }]);

    private Task WriteChunkAsync(ChatCompletionStreamChoice[] choices, ChatCompletionUsage? usage = null) => FrameAsync(JsonSerializer.Serialize(
        new ChatCompletionUpdate { Id = _id, Created = _created, Model = model, Choices = choices, Usage = usage }, includeUsage ? JsonSerializerOptions.Default : OmitNull));

    private Task EventAsync(MessageStreamPart part) => FrameAsync(JsonSerializer.Serialize(part, MessagesJson.Default), part.Type);

    private async Task FrameAsync(string data, string? name = null)
    {
        await response.WriteAsync(name is null ? $"data: {data}\n\n" : $"event: {name}\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}

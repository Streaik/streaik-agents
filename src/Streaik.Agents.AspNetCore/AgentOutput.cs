using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Responses.Streaming;
using Streaik.Tools;
using Streaik.Vercel.Models;
using Streaik.Vercel.Security;

namespace Streaik.Agents.AspNetCore;

internal sealed class AgentOutput(HttpResponse response, string name, bool ui, CancellationToken cancellationToken,
    AgentRunAccounting accounting, TimeProvider clock, long? started, ToolApprovalSigner? signer = null)
{
    private readonly string _id = $"resp_{Guid.NewGuid():N}";
    private readonly string _messageId = $"msg_{Guid.NewGuid():N}";
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly StringBuilder _text = new();
    private long? _firstDelta;
    private long _lastDelta;
    private readonly HashSet<string> _browserCalls = new(StringComparer.Ordinal);
    private readonly List<ResponseInputItem> _nativeItems = [];
    private bool _review;
    private int _sequence;

    public ResponseResult Snapshot(string status, string? text = null, ResponseResultError? error = null,
        IEnumerable<ResponseInputItem>? nativeItems = null)
    {
        var (usage, cost) = status == "completed" ? accounting.Snapshot() : default;
        return new()
        {
            Id = _id,
            Model = name,
            CreatedAt = _created,
            CompletedAt = status == "completed" ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null,
            Status = status,
            Store = false,
            Error = error,
            Output = [.. (string.IsNullOrEmpty(text) ? Array.Empty<ResponseInputItem>() : new ResponseInputItem[] { new ResponseInputMessage
            {
                Id = _messageId, Role = ResponseRole.Assistant, Status = status,
                Content = new ResponseMessageContent([new OutputTextPart(text)])
            } }), .. (nativeItems ?? _nativeItems)],
            Usage = usage,
            Metadata = status == "completed" && GatewayMetadata(usage, cost) is { } gateway ? new() { ["gateway"] = gateway } : null
        };
    }

    public async Task StartAsync()
    {
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        if (ui)
        {
            response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
            await EventAsync(new { type = "start", messageId = _messageId });
        }
        else await EventAsync(new ResponseCreated { Response = Snapshot("in_progress"), SequenceNumber = _sequence++ });
    }

    public async Task WriteAsync(AgentResponseUpdate update)
    {
        foreach (var part in update.Contents)
        {
            if (ui && part is HostedToolActivityContent hosted)
                await EventAsync<UIMessagePart>(hosted.Activity switch
                {
                    { Error: { } error } activity => new ToolOutputErrorPart { ToolCallId = activity.CallId, ErrorText = error, ProviderExecuted = true, Dynamic = true },
                    { Output: { } output } activity => new ToolOutputAvailablePart { ToolCallId = activity.CallId, Output = output, ProviderExecuted = true, Dynamic = true },
                    var activity => new ToolCallPart { ToolCallId = activity.CallId, ToolName = activity.Name, Input = activity.Input, ProviderExecuted = true, Dynamic = true,
                        ToolMetadata = new() { [HostedToolActivity.UiMetadataKey] = JsonSerializer.SerializeToElement(true) } }
                });
            if (ui && part is BrowserFunctionCall function && _browserCalls.Add(function.CallId))
            {
                var call = new ToolCallPart { ToolCallId = function.CallId, ToolName = function.Name, Input = function.Arguments ?? new Dictionary<string, object?>(),
                    ProviderExecuted = false, Dynamic = true };
                var id = Guid.NewGuid().ToString("N");
                await EventAsync(call);
                await EventAsync(new ToolApprovalRequestUIPart { ToolCallId = call.ToolCallId, ApprovalId = id,
                    Signature = signer?.CreateSignature(id, call) });
            }
            if (!ui && part is NativeResponseContent native)
            {
                _nativeItems.Add(native.Item);
                var item = JsonSerializer.SerializeToElement(native.Item, ResponseJson.Default).Deserialize<ResponseStreamItem>(ResponseJson.Default)!;
                await EventAsync(new ResponseOutputItemDone { Item = item, OutputIndex = _nativeItems.Count - 1 + (_text.Length > 0 ? 1 : 0),
                    SequenceNumber = _sequence++ });
            }
            if (ui && part is AgentExecutionApproval.Request pending)
            {
                foreach (var frame in pending.Parts)
                {
                    if (frame is ToolApprovalRequestUIPart) _review = true;
                    await EventAsync<UIMessagePart>(frame);
                }
            }
            if (part is not TextContent { Text.Length: > 0 } text) continue;
            if (_text.Length == 0) await StartTextAsync();
            _text.Append(text.Text);
            _lastDelta = clock.GetTimestamp();
            _firstDelta ??= _lastDelta;
            if (ui) await EventAsync(new TextDeltaUIMessageStreamPart { Id = _messageId, Delta = text.Text });
            else await EventAsync(new ResponseOutputTextDelta { ItemId = _messageId, SequenceNumber = _sequence++, Delta = text.Text });
        }
    }

    public async Task CompleteAsync()
    {
        if (ui)
        {
            if (_text.Length != 0) await EventAsync(new TextEndUIMessageStreamPart { Id = _messageId });
            var (usage, cost) = accounting.Snapshot();
            await EventAsync(new FinishUIPart
            {
                FinishReason = _browserCalls.Count == 0 && !_review ? "stop" : "tool-calls",
                MessageMetadata = new()
                {
                    Model = name, Timestamp = DateTimeOffset.UtcNow,
                    Usage = new() { PromptTokens = usage?.InputTokens, CompletionTokens = usage?.OutputTokens, TotalTokens = usage?.TotalTokens },
                    ProviderMetadata = GatewayMetadata(usage, cost) is { } gateway ? new() { ["gateway"] = gateway } : null
                }
            });
        }
        else
        {
            var text = _text.ToString();
            if (_text.Length != 0)
            {
                await EventAsync(new ResponseOutputTextDone { ItemId = _messageId, Text = text, SequenceNumber = _sequence++ });
                await EventAsync(new ResponseContentPartDone { ItemId = _messageId, Part = TextPart(text), SequenceNumber = _sequence++ });
                await EventAsync(new ResponseOutputItemDone { Item = Item("completed", text), SequenceNumber = _sequence++ });
            }
            await EventAsync(new ResponseCompleted { Response = Snapshot("completed", text), SequenceNumber = _sequence++ });
        }
        await SendAsync("data: [DONE]\n\n");
    }

    public async Task FailAsync(string code, string message)
    {
        if (ui) await EventAsync(new { type = "error", errorText = message });
        else await EventAsync(new ResponseFailed
        {
            Response = Snapshot("failed", error: new ResponseResultError { Code = code, Message = message }),
            SequenceNumber = _sequence++
        });
        await SendAsync("data: [DONE]\n\n");
    }

    private async Task StartTextAsync()
    {
        if (ui) await EventAsync(new TextStartUIMessageStreamPart { Id = _messageId });
        else
        {
            await EventAsync(new ResponseOutputItemAdded { Item = Item("in_progress"), SequenceNumber = _sequence++ });
            await EventAsync(new ResponseContentPartAdded { ItemId = _messageId, Part = TextPart(""), SequenceNumber = _sequence++ });
        }
    }

    private ResponseStreamItem Item(string status, string? text = null) => new()
    {
        Id = _messageId,
        Type = "message",
        Role = "assistant",
        Status = status,
        Content = JsonSerializer.SerializeToElement(text is null ? [] : new[] { TextPart(text) }, ResponseJson.Default)
    };

    private static ResponseStreamContentPart TextPart(string text) => new() { Type = "output_text", Text = text, Annotations = [] };

    private JsonElement? GatewayMetadata(ResponseUsage? usage, decimal? cost)
    {
        var gateway = new Dictionary<string, object>();
        if (cost is { } amount) gateway["cost"] = amount;
        // Match the gateway: first outgoing content delta, then output tokens over the first-to-last delta interval.
        if (started is { } start && _firstDelta is { } first)
        {
            gateway["ttft"] = clock.GetElapsedTime(start, first).TotalSeconds;
            var duration = clock.GetElapsedTime(first, _lastDelta).TotalSeconds;
            if (usage?.OutputTokens is { } tokens && duration > 0) gateway["tps"] = tokens / duration;
        }
        return gateway.Count == 0 ? null : JsonSerializer.SerializeToElement(gateway);
    }

    private Task EventAsync<T>(T part)
    {
        var prefix = part is ResponseStreamPart native ? $"event: {native.Type}\n" : "";
        return SendAsync($"{prefix}data: {JsonSerializer.Serialize(part, ResponseJson.Default)}\n\n");
    }

    private async Task SendAsync(string frame)
    {
        await response.WriteAsync(frame, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

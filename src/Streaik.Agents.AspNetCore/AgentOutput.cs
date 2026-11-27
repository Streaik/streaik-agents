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

internal sealed class AgentOutput(HttpResponse response, string name, bool ui, CancellationToken cancellationToken, ToolApprovalSigner? signer = null)
{
    private readonly string _id = $"resp_{Guid.NewGuid():N}";
    private readonly string _messageId = $"msg_{Guid.NewGuid():N}";
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly StringBuilder _text = new();
    private UsageDetails? _usage;
    private readonly HashSet<string> _browserCalls = new(StringComparer.Ordinal);
    private readonly List<ResponseInputItem> _nativeItems = [];
    private bool _review;
    private int _sequence;

    public ResponseResult Snapshot(string status, string? text = null, UsageDetails? usage = null, ResponseResultError? error = null,
        IEnumerable<ResponseInputItem>? nativeItems = null) => new()
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
        Usage = usage is null ? null : new ResponseUsage
        {
            InputTokens = (int?)usage.InputTokenCount,
            OutputTokens = (int?)usage.OutputTokenCount,
            TotalTokens = (int?)usage.TotalTokenCount
        }
    };

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
            if (part is UsageContent usage) _usage = usage.Details;
            if (ui && part is BrowserFunctionCall function && _browserCalls.Add(function.CallId))
            {
                var call = new ToolCallPart { ToolCallId = function.CallId, ToolName = function.Name, Input = function.Arguments ?? new Dictionary<string, object?>(),
                    ProviderExecuted = false, Dynamic = true };
                var id = Guid.NewGuid().ToString("N");
                await EventAsync(call);
                await EventAsync(new ToolApprovalRequestUIPart { ToolCallId = call.ToolCallId, ApprovalId = id,
                    Signature = signer!.CreateSignature(id, call) });
            }
            if (!ui && part is NativeResponseContent native)
            {
                _nativeItems.Add(native.Item);
                var item = JsonSerializer.SerializeToElement(native.Item, ResponseJson.Default).Deserialize<ResponseStreamItem>(ResponseJson.Default)!;
                await EventAsync(new ResponseOutputItemDone { Item = item, OutputIndex = _nativeItems.Count - 1 + (_text.Length > 0 ? 1 : 0),
                    SequenceNumber = _sequence++ });
            }
            if (ui && part is McpReviewContent reviewed)
            {
                _review = true;
                var review = reviewed.Review;
                var call = new ToolCallPart { ToolCallId = review.Id, ToolName = McpResultReviews.ToolName,
                    Title = $"Review MCP result: {review.Name}", ProviderExecuted = true, Dynamic = true,
                    Input = review.Input, ToolMetadata = new() { ["mcpReviewOutput"] = review.Output!.Value } };
                await EventAsync(call);
                await EventAsync(new ToolApprovalRequestUIPart { ToolCallId = review.Id, ApprovalId = review.Id,
                    Reason = "Review the completed MCP result before sending it to the model.",
                    ApprovalDescriptor = new { server = review.Server, name = review.Name, input = review.Input, output = review.Output } });
            }
            if (part is not TextContent { Text.Length: > 0 } text) continue;
            if (_text.Length == 0) await StartTextAsync();
            _text.Append(text.Text);
            if (ui) await EventAsync(new TextDeltaUIMessageStreamPart { Id = _messageId, Delta = text.Text });
            else await EventAsync(new ResponseOutputTextDelta { ItemId = _messageId, SequenceNumber = _sequence++, Delta = text.Text });
        }
    }

    public async Task CompleteAsync()
    {
        if (ui)
        {
            if (_text.Length != 0) await EventAsync(new TextEndUIMessageStreamPart { Id = _messageId });
            await EventAsync(new { type = "finish", finishReason = _browserCalls.Count == 0 && !_review ? "stop" : "tool-calls" });
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
            await EventAsync(new ResponseCompleted { Response = Snapshot("completed", text, _usage), SequenceNumber = _sequence++ });
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

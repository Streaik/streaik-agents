using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Responses.Extensions;
using Streaik.Responses.Streaming;

namespace Streaik.Agents;

internal sealed class ResponsesChatClient(HttpClient gateway, string model, IReadOnlyDictionary<string, string>? headers) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await gateway.GetResponses(CreateRequest(messages, options, stream: false), providerId: null,
            headers: headers, ct: cancellationToken);
        CheckResult(result);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, GetText(result)))
        {
            ModelId = result.Model,
            ResponseId = result.Id,
            FinishReason = ChatFinishReason.Stop,
            Usage = GetUsage(result)
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var hasText = false;
        await foreach (var part in gateway.GetResponsesUpdates(CreateRequest(messages, options, stream: true),
            providerId: string.Empty, headers: headers, ct: cancellationToken))
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
                    var terminal = new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        ModelId = completed.Response.Model,
                        ResponseId = completed.Response.Id,
                        FinishReason = ChatFinishReason.Stop
                    };
                    if (GetUsage(completed.Response) is { } usage)
                        terminal.Contents.Add(new UsageContent(usage));
                    yield return terminal;
                    yield break;
                case ResponseFailed or ResponseError or ResponseIncompleteEvent:
                    throw new InvalidOperationException("The upstream did not complete the response.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The upstream stream closed without a terminal response.");
    }

    private ResponseRequest CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream) => new()
    {
        Model = model,
        Instructions = options?.Instructions,
        Input = new ResponseInput(messages.Select(message => new ResponseInputMessage
        {
            Role = message.Role == ChatRole.Assistant ? ResponseRole.Assistant
                : message.Role == ChatRole.System ? ResponseRole.System : ResponseRole.User,
            Content = new ResponseMessageContent(message.Contents.OfType<TextContent>().Select(text =>
                message.Role == ChatRole.Assistant ? (ResponseContentPart)new OutputTextPart(text.Text) : new InputTextPart(text.Text)))
        })),
        Temperature = options?.Temperature,
        TopP = options?.TopP,
        MaxOutputTokens = options?.MaxOutputTokens,
        Stream = stream,
        Store = false
    };

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

using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Vercel.Models;

namespace Streaik.Agents.AspNetCore;

internal sealed record AgentInput(AgentDefinition Agent, IReadOnlyList<ChatMessage> Messages, bool Stream, ChatOptions? Options = null)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static AgentInput Parse(JsonElement body, AgentCatalog catalog, bool ui)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new AgentRequestException("The request body must be a JSON object.");

        RejectFields(body, "models", "workflowType", "workflowFile", "workflowMetadata", "workflow_type", "workflow_file", "workflow_metadata",
            "conversation", "previous_response_id", "tool_choice", "toolChoice");
        if (body.TryGetProperty("background", out var background) && background.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)
            || body.TryGetProperty("store", out var store) && store.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new AgentRequestException("Only foreground, stateless runs are supported.");
        if (body.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Null
            && !(tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0))
            throw new AgentRequestException("Tools are not supported in v1.");

        var name = body.TryGetProperty("model", out var model) ? model.Deserialize<string>(WebJson) : null;
        var inline = body.TryGetProperty("agent", out var definition) ? definition.Deserialize<AgentDefinition>(WebJson) : null;
        if (body.TryGetProperty("agents", out var agents) && agents.ValueKind != JsonValueKind.Null)
        {
            if (body.TryGetProperty("agent", out _) || agents.ValueKind != JsonValueKind.Array || agents.GetArrayLength() != 1
                || agents[0].ValueKind != JsonValueKind.Object)
                throw new AgentRequestException("Only one inline agent is supported.");
            inline = agents[0].Deserialize<AgentDefinition>(WebJson);
        }
        var agent = catalog.Resolve(name, inline);

        if (ui)
        {
            var messages = body.TryGetProperty("messages", out var value) ? value.Deserialize<UIMessage[]>(WebJson) : null;
            return new AgentInput(agent, ValidateMessages(messages?.Select(MapUiMessage)), Stream: true);
        }

        var request = body.Deserialize<ResponseRequest>(ResponseJson.Default)!;
        // Run instructions supplement, rather than replace, the selected agent's instructions.
        var history = MapResponseInput(request.Input);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            history.Insert(0, new ChatMessage(ChatRole.System, request.Instructions));
        if (request.MaxOutputTokens is <= 0)
            throw new AgentRequestException("'max_output_tokens' must be positive.");
        return new AgentInput(agent, ValidateMessages(history), request.Stream == true, new ChatOptions
        {
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = (float?)request.Temperature,
            TopP = (float?)request.TopP
        });
    }

    private static void RejectFields(JsonElement body, params string[] names)
    {
        foreach (var property in body.EnumerateObject())
            if (property.Value.ValueKind != JsonValueKind.Null && names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw new AgentRequestException($"'{property.Name}' is not supported in v1.");
    }

    private static List<ChatMessage> ValidateMessages(IEnumerable<ChatMessage>? messages)
    {
        var history = messages?.Where(message => message.Contents.Count > 0).ToList() ?? [];
        if (!history.Any(message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text)))
            throw new AgentRequestException("Provide at least one user text message.");
        return history;
    }

    private static ChatMessage MapUiMessage(UIMessage? message)
    {
        if (message?.Parts is null)
            throw new AgentRequestException("A chat message must include 'parts'.");
        var role = message.Role switch
        {
            Role.assistant => ChatRole.Assistant,
            Role.system => ChatRole.System,
            Role.user => ChatRole.User,
            _ => throw new AgentRequestException("Unsupported chat message role.")
        };
        var content = new List<AIContent>();
        foreach (var part in message.Parts)
        {
            if (part is TextUIPart { Text: { } text }) content.Add(new TextContent(text));
            else if (part?.Type is not ("step-start" or "reasoning"))
                throw new AgentRequestException("Only text chat message parts are supported.");
        }
        return new ChatMessage(role, content);
    }

    private static List<ChatMessage> MapResponseInput(ResponseInput? input)
    {
        if (input?.Text is { } text) return [new ChatMessage(ChatRole.User, text)];
        if (input?.Items is not { } items) throw new AgentRequestException("'input' is required.");

        var messages = new List<ChatMessage>();
        foreach (var item in items)
        {
            if (item is not ResponseInputMessage { Content: { } content } message)
                throw new AgentRequestException("Only text message input items are supported.");
            var role = message.Role switch
            {
                ResponseRole.Assistant => ChatRole.Assistant,
                ResponseRole.System or ResponseRole.Developer => ChatRole.System,
                ResponseRole.User => ChatRole.User,
                _ => throw new AgentRequestException("Unsupported Responses message role.")
            };
            List<AIContent> parts = content.Text is { } plain ? [new TextContent(plain)] : (content.Parts ?? []).Select<ResponseContentPart, AIContent>(part => part switch
            {
                InputTextPart { Text: { } value } => new TextContent(value),
                OutputTextPart { Text: { } value } when role == ChatRole.Assistant => new TextContent(value),
                _ => throw new AgentRequestException("Only text Responses content parts are supported.")
            }).ToList();
            messages.Add(new ChatMessage(role, parts));
        }
        return messages;
    }
}

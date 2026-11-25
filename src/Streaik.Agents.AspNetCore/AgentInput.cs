using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Vercel.Models;

namespace Streaik.Agents.AspNetCore;

internal sealed record AgentInput(IReadOnlyList<AgentDefinition> Agents, AgentWorkflow Workflow,
    IReadOnlyList<ChatMessage> Messages, bool Stream, ChatOptions? Options = null)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static async Task<AgentInput> ParseAsync(JsonElement body, AgentCatalog catalog, bool ui, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new AgentRequestException("The request body must be a JSON object.");

        var metadataField = ui ? "workflowMetadata" : "workflow_metadata";
        var wrongSpelling = ui ? "workflow_metadata" : "workflowMetadata";
        if (body.EnumerateObject().Any(field => field.Name.Equals(wrongSpelling, StringComparison.OrdinalIgnoreCase)))
            throw new AgentRequestException($"Use '{metadataField}' on this endpoint, not '{wrongSpelling}'.");

        RejectFields(body, "workflowFile", "workflow_file",
            "conversation", "previous_response_id", "tool_choice", "toolChoice");
        if (body.TryGetProperty("background", out var background) && background.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)
            || body.TryGetProperty("store", out var store) && store.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new AgentRequestException("Only foreground, stateless runs are supported.");
        if (body.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Null
            && !(tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0))
            throw new AgentRequestException("Tools are not supported in v1.");

        AgentSelection selection = ui ? body.Deserialize<AgentChatRequest>(WebJson)! : body.Deserialize<AgentResponsesRequest>(WebJson)!;
        var metadata = selection is AgentChatRequest chatRequest ? chatRequest.WorkflowMetadata : ((AgentResponsesRequest)selection).WorkflowMetadata;
        var agents = new List<AgentDefinition>();
        if (selection.Agent is { } inline) agents.Add(ValidateInline(inline));
        foreach (var agent in selection.Agents ?? [])
            agents.Add(ValidateInline(agent ?? throw new AgentRequestException("'agents' must contain agent definitions.")));
        var names = selection.Models ?? [];
        if (selection.Model is { } model) names = [.. names, model];
        agents.AddRange(await catalog.ResolveAsync(names, cancellationToken));
        if (agents.Count == 0)
            throw new AgentRequestException("Select at least one agent using 'model', 'models', 'agent', or 'agents'.");
        var workflow = AgentWorkflow.Validate(selection.WorkflowType ?? selection.WorkflowTypeSnakeCase, metadata, agents, metadataField);

        if (selection is AgentChatRequest chat)
            return new AgentInput(agents, workflow, ValidateMessages(chat.Messages?.Select(MapUiMessage)), Stream: true);

        var request = (AgentResponsesRequest)selection;
        // Run instructions supplement, rather than replace, the selected agent's instructions.
        var history = MapResponseInput(request.Input);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            history.Insert(0, new ChatMessage(ChatRole.System, request.Instructions));
        if (request.MaxOutputTokens is <= 0)
            throw new AgentRequestException("'max_output_tokens' must be positive.");
        return new AgentInput(agents, workflow, ValidateMessages(history), request.Stream == true, new ChatOptions
        {
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = (float?)request.Temperature,
            TopP = (float?)request.TopP
        });
    }

    private static AgentDefinition ValidateInline(AgentDefinition agent)
    {
        AgentRuntime.Validate(agent);
        return agent;
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

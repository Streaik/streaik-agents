using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Streaik.Responses;
using Streaik.Tools;
using Streaik.Vercel.Models;

namespace Streaik.Agents.AspNetCore;

internal sealed record AgentInput(IReadOnlyList<Agent> Agents, AgentWorkflow Workflow,
    IReadOnlyList<ChatMessage> Messages, bool Stream, ChatOptions? Options = null, AgentBrowserTools? Browser = null,
    AgentMcpApprovals? Mcp = null, bool IncludeUsage = false)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static async Task<AgentInput> ParseAsync(JsonElement body, AgentCatalog catalog, bool ui, HttpContext context, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new AgentRequestException("The request body must be a JSON object.");

        var chatFields = (AgentChatRequest.WorkflowTypeField, AgentChatRequest.WorkflowMetadataField);
        var responsesFields = (AgentResponsesRequest.WorkflowTypeField, AgentResponsesRequest.WorkflowMetadataField);
        var (typeField, metadataField) = ui ? chatFields : responsesFields;
        var (alternateTypeField, alternateMetadataField) = ui ? responsesFields : chatFields;
        RejectAlternateField(body, typeField, alternateTypeField);
        RejectAlternateField(body, metadataField, alternateMetadataField);

        RejectFields(body, "workflowFile", "workflow_file",
            "conversation", "previous_response_id", "tool_choice", "toolChoice");
        if (body.TryGetProperty("background", out var background) && background.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)
            || body.TryGetProperty("store", out var store) && store.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new AgentRequestException("Only foreground, stateless runs are supported.");
        if (!ui && body.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Null
            && !(tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0))
            throw new AgentRequestException("Tools are not supported in v1.");

        var chat = ui ? body.Deserialize<AgentChatRequest>(WebJson) : null;
        var responses = ui ? null : body.Deserialize<AgentResponsesRequest>(WebJson);
        AgentSelection selection = chat is not null ? chat : responses!;
        var agents = new List<Agent>();
        if (selection.Agent is { } inline) agents.Add(ValidateInline(inline));
        foreach (var agent in selection.Agents ?? [])
            agents.Add(ValidateInline(agent ?? throw new AgentRequestException("'agents' must contain agent definitions.")));
        var names = selection.Models ?? [];
        if (selection.Model is { } model) names = [.. names, model];
        agents.AddRange(await catalog.ResolveAsync(names, cancellationToken));
        if (agents.Count == 0)
            throw new AgentRequestException("Select at least one agent using 'model', 'models', 'agent', or 'agents'.");
        var workflow = AgentWorkflow.Validate(chat?.WorkflowType ?? responses?.WorkflowType,
            chat?.WorkflowMetadata ?? responses?.WorkflowMetadata, agents, typeField, metadataField);
        var nativeTools = agents.SelectMany(agent => AgentHostedTools.Read(agent).NativeMcp).ToArray();
        if (agents.Count != 1 && nativeTools.Length != 0)
            throw new AgentRequestException("Provider MCP approval is supported only in single-agent runs.");
        AgentMcpApprovals? mcp = null;
        if (agents.Count == 1)
        {
            var subject = McpResultReviews.Subject(context.User);
            var scope = subject is null ? null : Hash(new { subject, agent = agents[0] });
            var reviewScope = scope is not null && chat?.Id is { Length: > 0 and <= 256 } id ? Hash(new { scope, id }) : null;
            var services = context.RequestServices;
            var chatScope = chat?.Id is { Length: > 0 and <= 256 } chatId ? Hash(new { subject, chatId, agent = agents[0] }) : null;
            var signing = services.GetService<AgentToolApprovalSigning>();
            if (signing is not null && context.User.Identity?.IsAuthenticated == true && subject is null)
                throw new AgentRequestException("Tool approval signing is unavailable for this caller.");
            mcp = new AgentMcpApprovals(ui, scope, reviewScope, services.GetRequiredService<McpApprovalDecisions>(),
                services.GetRequiredService<McpResultReviews>(), nativeTools, new AgentExecutionApproval(chatScope,
                    signing is null ? null : new Streaik.Vercel.Security.ToolApprovalSigner(signing.Secret, subject), AgentHostedTools.Read(agents[0])));
        }

        if (chat is not null)
        {
            var browser = new AgentBrowserTools(context, chat, agents);
            return new AgentInput(agents, workflow, ValidateMessages(chat.Messages?.Select(message => MapUiMessage(message, browser, mcp,
                ReferenceEquals(message, chat.Messages.LastOrDefault())))),
                Stream: true, Options: browser.Options, Browser: browser, Mcp: mcp);
        }

        var request = responses!;
        // Run instructions supplement, rather than replace, the selected agent's instructions.
        var history = MapResponseInput(request.Input);
        var replies = history.SelectMany(message => message.Contents.OfType<NativeResponseContent>())
            .Select(content => content.Item).OfType<ResponseMcpApprovalResponseItem>().ToArray();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            history.Insert(0, new ChatMessage(ChatRole.System, request.Instructions));
        if (request.MaxOutputTokens is <= 0)
            throw new AgentRequestException("'max_output_tokens' must be positive.");
        var validated = ValidateMessages(history);
        if (replies.Length > 0)
        {
            if (mcp is null) throw new AgentRequestException("MCP approval continuation is only supported in single-agent runs.");
            mcp.ValidateNative(replies);
        }
        return new AgentInput(agents, workflow, validated, request.Stream == true, new ChatOptions
        {
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = (float?)request.Temperature,
            TopP = (float?)request.TopP
        }, Mcp: mcp);
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, WebJson)));

    private static Agent ValidateInline(Agent agent)
    {
        AgentRuntime.Validate(agent);
        return agent;
    }

    private static void RejectAlternateField(JsonElement body, string name, string alternate)
    {
        if (body.EnumerateObject().Any(field => field.Name.Equals(alternate, StringComparison.OrdinalIgnoreCase)))
            throw new AgentRequestException($"Use '{name}' on this endpoint, not '{alternate}'.");
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

    private static ChatMessage MapUiMessage(UIMessage? message, AgentBrowserTools browser, AgentMcpApprovals? mcp, bool active)
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
            else if (part is ToolInvocationPart decision && AgentExecutionApproval.IsContinuation(decision) && mcp?.Execution is not null)
            {
                if (active || Streaik.Vercel.Security.HostedToolContinuation.IsResult(decision)) content.Add(new AgentExecutionApproval.Decision(decision, message.Role));
            }
            else if (part is ToolInvocationPart review && AgentMcpApprovals.IsReview(review) && mcp is not null)
                content.Add(mcp.Replay(message.Role, review));
            else if (part is ToolInvocationPart invocation) browser.Replay(message.Role, invocation, content);
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
            if (item is ResponseMcpApprovalResponseItem decision)
            {
                messages.Add(new ChatMessage(ChatRole.User, [new NativeResponseContent(decision)]));
                continue;
            }
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

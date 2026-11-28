using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Streaik.Agents.AspNetCore;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Responses;

namespace Streaik.Agents.HeaderAuth;

internal static class AgentOpenApi
{
    private const string CapabilityDocumentName = "capabilities";
    private const string ProviderAuth = "Use x-<provider>-key for each provider called, including the decision model. "
        + "Authorization: Bearer <provider API key> is accepted as a fallback only when all selected models use one provider.";
    private const string Routing = "Select catalog agents with model/models, or provide full inline definitions with agent/agents. "
        + "Multiple participants default to sequential. Input is text only; runs are stateless.";

    internal static void AddAgentOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Configure);
        services.AddOpenApi(CapabilityDocumentName, options =>
        {
            Configure(options);
            options.AddDocumentTransformer(GroupCapabilitiesAsync);
        });
    }

    internal static void MapAgentReference(this WebApplication app)
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options => options
            .AddDocument("v1", "By API family", isDefault: true)
            .AddDocument(CapabilityDocumentName, "By capability"));
    }

    private static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Streaik Agents";
            document.Info.Description = Routing + " " + ProviderAuth;
            document.Components ??= new();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["providerBearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", Description = ProviderAuth
            };
            // OpenAPI cannot represent a header name selected dynamically from the request's provider IDs.
            document.Security = [new() { [new OpenApiSecuritySchemeReference("providerBearer", document)] = [] }, new()];
            return Task.CompletedTask;
        });
        options.AddOperationTransformer(async (operation, context, ct) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AgentRequestBodyMetadata>().LastOrDefault() is not { } body) return;
            var ui = body.Type == typeof(AgentChatRequest);
            var completions = body.Type == typeof(ChatCompletionOptions);
            var messages = body.Type == typeof(MessagesRequest);
            var standard = completions || messages;
            operation.Description = standard
                ? $"Returns {(completions ? "OpenAI Chat Completions" : "Anthropic Messages")} JSON, or SSE when stream is true. "
                    + "Select catalog agents with model or models, or supply full inline definitions with agent or agents. Omit model when using another selector. "
                    + "Use workflow_type and workflow_metadata as on Responses; multiple participants default to sequential. "
                    + "If a response includes workflow_continuation, POST it back to the same endpoint with only the requested tool results and the same credentials. "
                    + "The checkpoint is single-use, expires after 10 minutes, and is available only on the original server process. "
                    + (messages ? "Provide a positive max_tokens. Use Streaik credentials, not x-api-key. " : "Only one choice is supported. ") + ProviderAuth
                : (ui ? "Returns AI SDK UI Message Stream events. Use workflowType and workflowMetadata for workflow options. "
                    : "Returns a Responses object, or Responses SSE events when stream is true. Use workflow_type and workflow_metadata for workflow options. ") + Routing + " " + ProviderAuth;
            var examples = new Dictionary<string, IOpenApiExample>
            {
                ["namedAgent"] = Example("Single catalog agent", completions
                    ? """{"model":"GreetingAgent","messages":[{"role":"user","content":"Say hi"}]}"""
                    : messages ? """{"model":"GreetingAgent","max_tokens":128,"messages":[{"role":"user","content":"Say hi"}]}"""
                    : ui ? """{"model":"GreetingAgent","messages":[{"id":"1","role":"user","parts":[{"type":"text","text":"Say hi"}]}]}"""
                    : """{"model":"GreetingAgent","input":"Say hi"}""")
            };
            if (standard)
            {
                examples["workflow"] = Example("Sequential catalog workflow without model", completions
                    ? """{"models":["GreetingAgent","ReviewAgent"],"workflow_type":"sequential","messages":[{"role":"user","content":"Write a short welcome note"}]}"""
                    : """{"models":["GreetingAgent","ReviewAgent"],"workflow_type":"sequential","max_tokens":128,"messages":[{"role":"user","content":"Write a short welcome note"}]}""");
                examples["continueWorkflow"] = Example("Resume with the tool result and the token from the previous response", completions
                    ? """{"workflow_continuation":"TOKEN_FROM_RESPONSE","messages":[{"role":"tool","tool_call_id":"call_weather","content":"sunny"}]}"""
                    : """{"workflow_continuation":"TOKEN_FROM_RESPONSE","max_tokens":128,"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_weather","content":"sunny"}]}]}""");
            }
            else
                examples["workflow"] = Example("Sequential inline workflow", ui
                    ? """{"agents":[{"name":"Draft","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Write a draft."},{"name":"Review","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Review the draft."}],"workflowType":"sequential","messages":[{"id":"1","role":"user","parts":[{"type":"text","text":"Write a short welcome note"}]}]}"""
                    : """{"agents":[{"name":"Draft","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Write a draft."},{"name":"Review","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Review the draft."}],"workflow_type":"sequential","input":"Write a short welcome note"}""");
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, IOpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = await context.GetOrCreateSchemaAsync(body.Type, cancellationToken: ct),
                        Examples = examples
                    }
                }
            };
            if (operation.Responses?.TryGetValue("200", out var success) == true && success.Content is { } content)
                content["text/event-stream"] = new OpenApiMediaType { Schema = Text() };
            operation.Responses ??= new();
            foreach (var (status, description) in new[] { ("400", "Invalid agent request"), ("401", "Missing or rejected credentials"),
                ("403", "Gateway authorization rejected"), ("404", "Named agent not found"), ("415", "Request must be JSON"),
                ("429", "Gateway rate limit reached"), ("502", "Gateway run failed"), ("503", "Agent catalog unavailable"), ("504", "Gateway timed out") })
                operation.Responses[status] = new OpenApiResponse { Description = description };
        });
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var type = context.JsonTypeInfo.Type;
            // This endpoint also accepts catalog lists and inline definitions through extension data.
            if (type == typeof(MessagesRequest)) schema.Required?.Remove("model");
            if (type == typeof(MessagesContent) || type == typeof(ChatMessageContent))
            {
                schema.Type = null;
                schema.AnyOf = [Text(), new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.Object } }];
                schema.Description = "Text or supported protocol content blocks.";
            }
            if (type == typeof(ResponseInput))
            {
                schema.Type = null;
                schema.AnyOf = [Text(), new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.Object } }];
                schema.Description = "User text, or an array of text-only Responses message items.";
            }

            var workflowField = type == typeof(AgentChatRequest) ? AgentChatRequest.WorkflowTypeField
                : type == typeof(AgentResponsesRequest) ? AgentResponsesRequest.WorkflowTypeField : null;
            if (workflowField is not null && schema.Properties?.TryGetValue(workflowField, out var property) == true && property is OpenApiSchema workflow)
            {
                workflow.Enum = ["sequential", "concurrent", "handoff", "groupchat", "magentic", "decisions"];
                workflow.Default = JsonValue.Create("sequential");
            }
            return Task.CompletedTask;
        });
    }

    private static Task GroupCapabilitiesAsync(OpenApiDocument document, OpenApiDocumentTransformerContext _, CancellationToken ct)
    {
        const string other = "Other";
        var pathsByCapability = document.Paths.Where(path => path.Value.Operations is { Count: > 0 }).ToLookup(path => path.Key switch
        {
            "/v1/responses" or "/api/chat" or "/v1/chat/completions" or "/v1/messages" => "Chat",
            "/v1/models" => "LLM",
            _ when path.Key.StartsWith("/v1/models/", StringComparison.Ordinal) => "LLM",
            _ => other
        });

        var groups = new JsonArray();
        document.Tags = new HashSet<OpenApiTag>();
        foreach (var capability in new[] { "Chat", "LLM", other })
        {
            var sectionNames = new JsonArray();
            var sections = pathsByCapability[capability].GroupBy(path => RoutePrefix(path.Key))
                .OrderBy(section => section.Key switch { "/v1" => 0, "/api" => 1, _ => 2 })
                .ThenBy(section => section.Key, StringComparer.Ordinal);
            foreach (var section in sections)
            {
                // Tags must be unique in the document, even when Scalar displays the same prefix in different groups.
                var name = $"{capability} {section.Key}";
                document.Tags.Add(new OpenApiTag
                {
                    Name = name,
                    Extensions = new Dictionary<string, IOpenApiExtension>
                    {
                        ["x-displayName"] = new JsonNodeExtension(JsonValue.Create(section.Key)!)
                    }
                });
                sectionNames.Add(name);
                foreach (var path in section)
                    foreach (var operation in path.Value.Operations!.Values)
                        operation.Tags = new HashSet<OpenApiTagReference> { new(name, document) };
            }
            if (sectionNames.Count > 0)
                groups.Add(new JsonObject { ["name"] = capability, ["tags"] = sectionNames });
        }
        (document.Extensions ??= new Dictionary<string, IOpenApiExtension>())["x-tagGroups"] = new JsonNodeExtension(groups);
        return Task.CompletedTask;
    }

    private static string RoutePrefix(string path)
    {
        var separator = path.IndexOf('/', 1);
        return separator < 0 ? path : path[..separator];
    }

    private static OpenApiSchema Text() => new() { Type = JsonSchemaType.String };
    private static OpenApiExample Example(string summary, string json) => new() { Summary = summary, Value = JsonNode.Parse(json) };
}

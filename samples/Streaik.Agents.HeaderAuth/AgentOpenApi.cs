using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Streaik.Agents.AspNetCore;
using Streaik.Responses;

namespace Streaik.Agents.HeaderAuth;

internal static class AgentOpenApi
{
    private const string ProviderAuth = "Use x-<provider>-key for each provider called, including the decision model. "
        + "Authorization: Bearer <provider API key> is accepted as a fallback only when all selected models use one provider.";
    private const string Routing = "Select catalog agents with model/models, or provide full inline definitions with agent/agents. "
        + "Multiple participants default to sequential. Input is text only; runs are stateless.";

    internal static void MapAgentReference(this WebApplication app)
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options => options.AddDocument("v1", "Streaik Agents", isDefault: true));
    }

    internal static void Configure(OpenApiOptions options)
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
            operation.Description = (ui ? "Returns AI SDK UI Message Stream events. Use workflowMetadata for workflow options. "
                : "Returns a Responses object, or Responses SSE events when stream is true. Use workflow_metadata for workflow options. ") + Routing + " " + ProviderAuth;
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, IOpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = await context.GetOrCreateSchemaAsync(body.Type, cancellationToken: ct),
                        Examples = new Dictionary<string, IOpenApiExample>
                        {
                            ["namedAgent"] = Example("Single catalog agent", ui
                                ? """{"model":"GreetingAgent","messages":[{"id":"1","role":"user","parts":[{"type":"text","text":"Say hi"}]}]}"""
                                : """{"model":"GreetingAgent","input":"Say hi"}"""),
                            ["workflow"] = Example("Sequential inline workflow", ui
                                ? """{"agents":[{"name":"Draft","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Write a draft."},{"name":"Review","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Review the draft."}],"workflowType":"sequential","messages":[{"id":"1","role":"user","parts":[{"type":"text","text":"Write a short welcome note"}]}]}"""
                                : """{"agents":[{"name":"Draft","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Write a draft."},{"name":"Review","model":{"id":"openai/gpt-4.1-mini"},"instructions":"Review the draft."}],"workflowType":"sequential","input":"Write a short welcome note"}""")
                        }
                    }
                }
            };
            if (operation.Responses?.TryGetValue("200", out var success) == true && success.Content is { } content)
                content["text/event-stream"] = new OpenApiMediaType { Schema = Text() };
            operation.Responses ??= new();
            foreach (var (status, description) in new[] { ("400", "Invalid agent request"), ("401", "Missing or rejected credentials"),
                ("403", "Gateway authorization rejected"), ("404", "Named agent not found"), ("415", "Request must be JSON"),
                ("429", "Gateway rate limit reached"), ("502", "Gateway run failed"), ("504", "Gateway timed out") })
                operation.Responses[status] = new OpenApiResponse { Description = description };
        });
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(ResponseInput))
            {
                schema.Type = null;
                schema.AnyOf = [Text(), new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.Object } }];
                schema.Description = "User text, or an array of text-only Responses message items.";
            }
            if (typeof(AgentSelection).IsAssignableFrom(context.JsonTypeInfo.Type) && schema.Properties is { } properties)
            {
                foreach (var name in new[] { "workflowType", "workflow_type" })
                    if (properties.TryGetValue(name, out var property) && property is OpenApiSchema workflow)
                    {
                        workflow.Enum = ["sequential", "concurrent", "handoff", "groupchat", "magentic", "decisions"];
                        workflow.Default = JsonValue.Create("sequential");
                    }
            }
            return Task.CompletedTask;
        });
    }

    private static OpenApiSchema Text() => new() { Type = JsonSchemaType.String };
    private static OpenApiExample Example(string summary, string json) => new() { Summary = summary, Value = JsonNode.Parse(json) };
}

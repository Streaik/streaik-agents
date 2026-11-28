using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Responses;

namespace Streaik.Agents.AspNetCore;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapStreaikAgents(this IEndpointRouteBuilder endpoints)
    {
        AgentModelEndpoints.Map(endpoints);
        endpoints.MapPost("/api/chat", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, ui: true))
            .WithName("StreamAgentChat").WithTags("Vercel")
            .WithMetadata(new AgentRequestBodyMetadata(typeof(AgentChatRequest)))
            .Produces<string>(200, "text/event-stream");
        endpoints.MapPost("/v1/responses", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, ui: false))
            .WithName("CreateAgentResponse").WithTags("OpenAI")
            .WithMetadata(new AgentRequestBodyMetadata(typeof(AgentResponsesRequest)))
            .Produces<ResponseResult>(200, "application/json", "text/event-stream");
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, AgentCatalog catalog, AgentRuntime runtime, bool ui)
    {
        var ct = context.RequestAborted;
        AgentInput input;
        try
        {
            if (!context.Request.HasJsonContentType())
            {
                await ErrorAsync(context, 415, "invalid_request", "Send the request as JSON.");
                return;
            }
            input = await AgentInput.ParseAsync(await context.Request.ReadFromJsonAsync<JsonElement>(ct), catalog, ui, context, ct);
        }
        catch (AgentRequestException error)
        {
            await ErrorAsync(context, error.Code == "agent_not_found" ? 404 : 400, error.Code, error.Message);
            return;
        }
        catch (JsonException)
        {
            await ErrorAsync(context, 400, "invalid_request", "The request contains malformed JSON or unsupported fields.");
            return;
        }
        catch (AgentCatalogUnavailableException)
        {
            await ErrorAsync(context, 503, "agent_catalog_unavailable", "The agent catalog is unavailable.");
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        AgentOutput? output = null;
        try
        {
            var accounting = new AgentRunAccounting();
            var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            long? started = input.Stream ? clock.GetTimestamp() : null;
            var credentials = context.RequestServices.GetService<IAgentGatewayHeaders>();
            var participants = input.Agents;
            var router = participants.Count > 1 ? input.Workflow.Router : null;
            var providers = (router is null ? participants : participants.Append(router)).SelectMany(AgentRuntime.PotentialProviders)
                .Concat(participants.SelectMany(AgentRuntime.SkillProviders));
            var singleProvider = providers.Distinct(StringComparer.Ordinal).Take(2).Count() == 1;
            var headerCache = new Dictionary<(Agent, AgentModel), IReadOnlyDictionary<string, string>>();

            async ValueTask<IReadOnlyDictionary<string, string>?> HeadersFor(Agent agent, AgentModel model, bool optional)
            {
                if (credentials is null) return AgentRuntime.EmptyHeaders;
                var key = (agent, model);
                if (headerCache.TryGetValue(key, out var headers)) return headers;
                try
                {
                    headers = await credentials.GetHeadersAsync(context, agent.ForModel(model), singleProvider, ct);
                    headerCache.Add(key, headers);
                    return headers;
                }
                catch (AgentGatewayAuthenticationException) when (optional && !ct.IsCancellationRequested) { return null; }
            }

            if (router is not null)
                participants = [await runtime.SelectAgentAsync(router, participants, input.Messages,
                    runtime.ModelHeaders(router.Model!, (await HeadersFor(router, router.Model!, false))!), ct, accounting)];
            // Resolve only the chosen agents, once per workflow run, before running answer inference.
            var targets = await runtime.ResolveTargetsAsync(participants, input.Messages, HeadersFor, ct, accounting);

            var primary = targets[0];
            output = new AgentOutput(context.Response, participants.Count == 1 ? primary.Definition.Name! : input.Workflow.Type,
                ui, ct, accounting, clock, started, input.Browser?.Signer);
            if (!input.Stream && participants.Count == 1)
            {
                var result = await runtime.RunResolvedAsync(primary, input.Messages, input.Options, ct, input.Mcp, accounting);
                var items = result.Messages.SelectMany(message => message.Contents.OfType<NativeResponseContent>()).Select(content => content.Item);
                await context.Response.WriteAsJsonAsync(output.Snapshot("completed", result.Text, nativeItems: items), ResponseJson.Default, ct);
                return;
            }

            var updates = participants.Count == 1
                ? runtime.StreamResolvedAsync(primary, input.Messages, input.Options, ct, input.Mcp, reportHostedActivity: ui, accounting: accounting)
                : runtime.RunWorkflowAsync(input.Workflow, targets, input.Messages.ToList(), input.Options, input.Stream, ct, accounting, reportHostedActivity: ui);
            if (!input.Stream)
            {
                var text = new StringBuilder();
                await foreach (var update in updates.WithCancellation(ct))
                    text.Append(update.Text);
                await context.Response.WriteAsJsonAsync(output.Snapshot("completed", text.ToString()), ResponseJson.Default, ct);
                return;
            }

            await using var stream = updates.GetAsyncEnumerator(ct);
            // Keep upstream connect/authorization failures as HTTP errors until an event is available.
            var more = await stream.MoveNextAsync();
            await output.StartAsync();
            while (more)
            {
                await output.WriteAsync(stream.Current);
                more = await stream.MoveNextAsync();
            }
            await output.CompleteAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (AgentRequestException error) when (!context.Response.HasStarted)
        {
            await ErrorAsync(context, 400, error.Code, error.Message);
        }
        catch (AgentGatewayAuthenticationException error) when (!context.Response.HasStarted)
        {
            if (!error.Forbidden) context.Response.Headers.WWWAuthenticate = "Bearer";
            await ErrorAsync(context, error.Forbidden ? 403 : 401, "gateway_authentication", error.Message);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Upstream errors may contain credentials or provider internals. Never echo their text.
            var (status, code, message) = error switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http =>
                    ((int)http.StatusCode!.Value, "upstream_authentication", "The gateway rejected the credentials."),
                HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                    (429, "rate_limit_exceeded", "The gateway rate limit was reached."),
                OperationCanceledException => (504, "upstream_timeout", "The gateway did not respond in time."),
                _ => (502, "upstream_error", "The gateway could not complete the agent run.")
            };
            if (context.Response.HasStarted)
            {
                if (input.Stream && output is not null) await output.FailAsync(code, message);
                else context.Abort();
            }
            else await ErrorAsync(context, status, code, message);
        }
    }

    private static Task ErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { code, message } }, context.RequestAborted);
    }
}

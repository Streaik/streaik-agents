using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.Responses;

namespace Streaik.Agents.AspNetCore;

internal enum AgentEndpointProtocol { Ui, Responses, ChatCompletions, Messages }

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapStreaikAgents(this IEndpointRouteBuilder endpoints)
    {
        AgentModelEndpoints.Map(endpoints);
        endpoints.MapPost("/api/chat", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, AgentEndpointProtocol.Ui))
            .WithName("StreamAgentChat").WithTags("Vercel")
            .WithMetadata(new AgentRequestBodyMetadata(typeof(AgentChatRequest)))
            .Produces<string>(200, "text/event-stream");
        endpoints.MapPost("/v1/responses", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, AgentEndpointProtocol.Responses))
            .WithName("CreateAgentResponse").WithTags("OpenAI")
            .WithMetadata(new AgentRequestBodyMetadata(typeof(AgentResponsesRequest)))
            .Produces<ResponseResult>(200, "application/json", "text/event-stream");
        endpoints.MapPost("/v1/chat/completions", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, AgentEndpointProtocol.ChatCompletions))
            .WithName("CreateAgentChatCompletion").WithTags("OpenAI")
            .WithMetadata(new AgentRequestBodyMetadata(typeof(ChatCompletionOptions)))
            .Produces<ChatCompletion>(200, "application/json", "text/event-stream");
        endpoints.MapPost("/v1/messages", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, AgentEndpointProtocol.Messages))
            .WithName("CreateAgentMessage").WithTags("Anthropic")
            .WithMetadata(new AgentRequestBodyMetadata(typeof(MessagesRequest)))
            .Produces<MessagesResponse>(200, "application/json", "text/event-stream");
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, AgentCatalog catalog, AgentRuntime runtime, AgentEndpointProtocol protocol)
    {
        var ui = protocol == AgentEndpointProtocol.Ui;
        var standard = protocol is AgentEndpointProtocol.ChatCompletions or AgentEndpointProtocol.Messages;
        var ct = context.RequestAborted;
        Task Error(int status, string code, string message) => standard
            ? AgentProtocolOutput.ErrorAsync(context, protocol, status, code, message)
            : ErrorAsync(context, status, code, message);
        AgentInput input;
        try
        {
            if (!context.Request.HasJsonContentType())
            {
                await Error(415, "invalid_request", "Send the request as JSON.");
                return;
            }
            var body = await context.Request.ReadFromJsonAsync<JsonElement>(ct);
            input = standard ? await AgentProtocolInput.ParseAsync(body, catalog, protocol, ct)
                : await AgentInput.ParseAsync(body, catalog, ui, context, ct);
        }
        catch (AgentRequestException error)
        {
            await Error(error.Code == "agent_not_found" ? 404 : 400, error.Code, error.Message);
            return;
        }
        catch (JsonException)
        {
            await Error(400, "invalid_request", "The request contains malformed JSON or unsupported fields.");
            return;
        }
        catch (AgentCatalogUnavailableException)
        {
            await Error(503, "agent_catalog_unavailable", "The agent catalog is unavailable.");
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        AgentOutput? output = null;
        AgentProtocolOutput? protocolOutput = null;
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
            var model = participants.Count == 1 ? primary.Definition.Name! : input.Workflow.Type;
            if (standard) protocolOutput = new(context.Response, protocol, model, ct, accounting, clock, input.IncludeUsage);
            else output = new(context.Response, model, ui, ct, accounting, clock, started, input.Browser?.Signer);
            if (!input.Stream && participants.Count == 1)
            {
                var result = await runtime.RunResolvedAsync(primary, input.Messages, input.Options, ct, input.Mcp, accounting, standard);
                if (protocolOutput is not null) await protocolOutput.WriteResultAsync(result);
                else
                {
                    var items = result.Messages.SelectMany(message => message.Contents.OfType<NativeResponseContent>()).Select(content => content.Item);
                    await context.Response.WriteAsJsonAsync(output!.Snapshot("completed", result.Text, nativeItems: items), ResponseJson.Default, ct);
                }
                return;
            }

            var updates = participants.Count == 1
                ? runtime.StreamResolvedAsync(primary, input.Messages, input.Options, ct, input.Mcp, reportHostedActivity: ui, accounting: accounting, standardProtocol: standard)
                : runtime.RunWorkflowAsync(input.Workflow, targets, input.Messages.ToList(), input.Options, input.Stream, ct, accounting, reportHostedActivity: ui);
            if (!input.Stream)
            {
                var text = new StringBuilder();
                await foreach (var update in updates.WithCancellation(ct))
                    text.Append(update.Text);
                await context.Response.WriteAsJsonAsync(output!.Snapshot("completed", text.ToString()), ResponseJson.Default, ct);
                return;
            }

            await using var stream = updates.GetAsyncEnumerator(ct);
            // Keep upstream connect/authorization failures as HTTP errors until an event is available.
            var more = await stream.MoveNextAsync();
            if (protocolOutput is not null) await protocolOutput.StartAsync();
            else await output!.StartAsync();
            while (more)
            {
                if (protocolOutput is not null) await protocolOutput.WriteAsync(stream.Current);
                else await output!.WriteAsync(stream.Current);
                more = await stream.MoveNextAsync();
            }
            if (protocolOutput is not null) await protocolOutput.CompleteAsync();
            else await output!.CompleteAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (AgentRequestException error) when (!context.Response.HasStarted)
        {
            await Error(400, error.Code, error.Message);
        }
        catch (AgentGatewayAuthenticationException error) when (!context.Response.HasStarted)
        {
            if (!error.Forbidden) context.Response.Headers.WWWAuthenticate = "Bearer";
            await Error(error.Forbidden ? 403 : 401, "gateway_authentication", error.Message);
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
                if (input.Stream && protocolOutput is not null) await protocolOutput.FailAsync(status, code, message);
                else if (input.Stream && output is not null) await output.FailAsync(code, message);
                else context.Abort();
            }
            else await Error(status, code, message);
        }
    }

    private static Task ErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { code, message } }, context.RequestAborted);
    }
}

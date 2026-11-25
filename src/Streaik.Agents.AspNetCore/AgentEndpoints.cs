using System.Net;
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
        endpoints.MapPost("/api/chat", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, ui: true));
        endpoints.MapPost("/v1/responses", (HttpContext context, AgentCatalog catalog, AgentRuntime runtime) =>
            HandleAsync(context, catalog, runtime, ui: false));
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
            input = AgentInput.Parse(await context.Request.ReadFromJsonAsync<JsonElement>(ct), catalog, ui);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        var output = new AgentOutput(context.Response, input.Agent.Name!, ui, ct);
        try
        {
            var credentials = context.RequestServices.GetService<IAgentGatewayHeaders>();
            var headers = credentials is null ? null : await credentials.GetHeadersAsync(context, input.Agent, ct);
            if (!input.Stream)
            {
                var result = await runtime.RunAsync(input.Agent, input.Messages, headers, input.Options, ct);
                await context.Response.WriteAsJsonAsync(output.Snapshot("completed", result.Text, result.Usage), ResponseJson.Default, ct);
                return;
            }

            await using var stream = runtime.StreamAsync(input.Agent, input.Messages, headers, input.Options, ct).GetAsyncEnumerator(ct);
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
                if (input.Stream) await output.FailAsync(code, message);
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

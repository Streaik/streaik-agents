using System.Net.Http.Json;
using System.Text.Json;
using Streaik.Responses;
using Streaik.Responses.Extensions;
using Streaik.Tools;

namespace Streaik.Agents;

public sealed partial class AgentRuntime
{
    private async Task<HostedSearchRoute?> ResolveHostedSearchAsync(AgentRunTarget target, string model, CancellationToken ct)
    {
        var slash = model.IndexOf('/');
        if (slash < 1) return null;
        var provider = model[..slash];
        if (!target.SearchHeaders.TryGetValue(provider, out var credentials)) return null;
        var headers = ScopeHeaders(credentials, new(StringComparer.Ordinal) { provider });
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(TimeSpan.FromSeconds(5));
        Dictionary<string, Dictionary<string, JsonElement>> catalog;
        try { catalog = await ListProviderModelsAsync(headers, lookup.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lookup.IsCancellationRequested) { return null; }
        ct.ThrowIfCancellationRequested();
        if (!catalog.TryGetValue(model, out var variants)) return null;
        if (variants.ContainsKey("decision")) return new(Decide: async (body, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/decisions") { Content = JsonContent.Create(body) };
            AddHeaders(request, headers);
            using var response = await gateway.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(token);
        });
        if (!variants.ContainsKey("language")) return null;
        return new(Complete: async (instructions, input, token) =>
        {
            var result = await gateway.GetResponses(new ResponseRequest
            {
                Model = model, Instructions = instructions, Input = new ResponseInput(input),
                Store = false, Stream = false, MaxOutputTokens = 2048,
            }, providerId: null, relativeUrl: Agent.DefaultTransport, headers: headers, ct: token);
            return result.Status == "completed" && result.Error is null ? ResponsesChatClient.GetText(result) : null;
        });
    }
}

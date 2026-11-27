using System.Net.Http.Headers;
using Streaik.Agents.AspNetCore;

namespace Streaik.Agents.HeaderAuth;

internal sealed class HeaderGatewayHeaders : IAgentGatewayHeaders
{
    public ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(HttpContext context, Agent agent, CancellationToken cancellationToken)
        => GetHeadersAsync(context, agent, singleProvider: true, cancellationToken);

    public ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(HttpContext context, Agent agent,
        bool singleProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = $"x-{AgentRuntime.GetProvider(agent)}-key";
        var requestHeaders = context.Request.Headers;
        string? key = null;
        if (requestHeaders.TryGetValue(name, out var values))
        {
            if (values.Count == 1) key = values[0]?.Trim();
        }
        else if (singleProvider && requestHeaders.Authorization.Count == 1
            && AuthenticationHeaderValue.TryParse(requestHeaders.Authorization[0], out var auth)
            && auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            key = auth.Parameter;

        if (string.IsNullOrWhiteSpace(key) || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ','))
            throw new AgentGatewayAuthenticationException(singleProvider
                ? $"Provide one provider API key in '{name}' or 'Authorization: Bearer'."
                : $"Provide one provider API key in '{name}' for this mixed-provider workflow.");

        // Normalize both input forms into the selected provider's gateway credential.
        // No other caller headers or authentication reach the gateway.
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [name] = key });
    }
}

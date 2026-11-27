using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Streaik.Tools;

namespace Streaik.Agents.AspNetCore;

public static class AgentHosting
{
    public const string GatewayClientName = "Streaik.Agents.Gateway";

    public static IServiceCollection AddStreaikAgents(this IServiceCollection services, IConfigurationSection configuration)
    {
        var gatewayBaseUrl = configuration["GatewayEndpoint"];
        if (!Uri.TryCreate(gatewayBaseUrl, UriKind.Absolute, out var gateway) || gateway.Host.Length == 0
            || (gateway.Scheme != Uri.UriSchemeHttps && !(gateway.Scheme == Uri.UriSchemeHttp && gateway.IsLoopback))
            || gateway.UserInfo.Length != 0 || gateway.Query.Length != 0 || gateway.Fragment.Length != 0)
            throw new InvalidOperationException("Configure GatewayEndpoint as the shared HTTPS gateway base URL (HTTP is allowed only on loopback).");

        var baseAddress = new Uri(gateway.AbsoluteUri.TrimEnd('/') + "/");
        services.AddSingleton<AgentCatalog>();
        services.TryAddSingleton(new McpResultReviews());
        services.TryAddSingleton(new McpApprovalDecisions());
        if (configuration["ToolApprovalSigningSecret"] is { } secret && !string.IsNullOrWhiteSpace(secret))
            services.TryAddSingleton(new AgentToolApprovalSigning(secret));
        services.AddHttpClient(GatewayClientName, client => client.BaseAddress = baseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped(services => new AgentRuntime(services.GetRequiredService<IHttpClientFactory>().CreateClient(GatewayClientName),
            services.GetService<HostedToolAccess>()));
        return services;
    }
}

internal sealed record AgentToolApprovalSigning(string Secret);

/// <summary>Supplies credentials for one agent run to the host-configured gateway only.</summary>
public interface IAgentGatewayHeaders
{
    ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(HttpContext context, Agent agent, CancellationToken cancellationToken);

    /// <summary>Indicates whether an unscoped provider API key is unambiguous for this request.</summary>
    ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(HttpContext context, Agent agent,
        bool singleProvider, CancellationToken cancellationToken) => GetHeadersAsync(context, agent, cancellationToken);
}

public sealed class AgentGatewayAuthenticationException(string message, bool forbidden = false) : Exception(message)
{
    public bool Forbidden { get; } = forbidden;
}

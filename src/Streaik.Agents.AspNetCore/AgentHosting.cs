using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Streaik.Agents.AspNetCore;

public static class AgentHosting
{
    public const string GatewayClientName = "Streaik.Agents.Gateway";

    public static IServiceCollection AddStreaikAgents(this IServiceCollection services, IConfigurationSection configuration)
    {
        var endpoint = configuration["GatewayEndpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var gateway) || gateway.Host.Length == 0
            || (gateway.Scheme != Uri.UriSchemeHttps && !(gateway.Scheme == Uri.UriSchemeHttp && gateway.IsLoopback))
            || gateway.UserInfo.Length != 0 || gateway.Query.Length != 0 || gateway.Fragment.Length != 0)
            throw new InvalidOperationException("Configure an HTTPS GatewayEndpoint (HTTP is allowed only on loopback).");

        var baseAddress = new Uri(gateway.AbsoluteUri.TrimEnd('/') + "/");
        services.AddSingleton(new AgentCatalog(configuration.GetSection("Agents").Get<AgentDefinition[]>() ?? []));
        services.AddHttpClient(GatewayClientName, client => client.BaseAddress = baseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped(services => new AgentRuntime(services.GetRequiredService<IHttpClientFactory>().CreateClient(GatewayClientName)));
        return services;
    }
}

/// <summary>Supplies credentials for one agent run to the host-configured gateway only.</summary>
public interface IAgentGatewayHeaders
{
    ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(HttpContext context, AgentDefinition agent, CancellationToken cancellationToken);
}

public sealed class AgentGatewayAuthenticationException(string message, bool forbidden = false) : Exception(message)
{
    public bool Forbidden { get; } = forbidden;
}

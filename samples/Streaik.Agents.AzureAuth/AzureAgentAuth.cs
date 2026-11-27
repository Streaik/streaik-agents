using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Streaik.Agents.AspNetCore;

namespace Streaik.Agents.AzureAuth;

internal static class AzureAgentAuth
{
    private const string Access = "Streaik.Agents.DelegatedAccess";
    private const string ScopeClaim = "http://schemas.microsoft.com/identity/claims/scope";

    public static void AddAzureAgentAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var requiredScope = configuration["AzureAd:RequiredScope"];
        var gatewayScopes = configuration.GetSection("StreaikAgents:GatewayScopes").Get<string[]>();
        if (string.IsNullOrWhiteSpace(requiredScope) || requiredScope.Any(char.IsWhiteSpace)
            || gatewayScopes is not { Length: > 0 } || gatewayScopes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Configure AzureAd:RequiredScope and at least one StreaikAgents:GatewayScopes value.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();
        services.AddAuthorizationBuilder().AddPolicy(Access, policy => policy.RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.Claims.Any(claim => claim.Type is "scp" or ScopeClaim
                && claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requiredScope, StringComparer.Ordinal))));
        services.AddScoped<IAgentGatewayHeaders>(provider =>
            new AzureGatewayHeaders(provider.GetRequiredService<ITokenAcquisition>(), gatewayScopes));
    }

    public static void MapAzureAuthAgents(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("").RequireAuthorization(Access).MapStreaikAgents();
    }
}

internal sealed class AzureGatewayHeaders(ITokenAcquisition acquisition, string[] scopes) : IAgentGatewayHeaders
{
    public async ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(HttpContext context, Agent agent, CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            throw new AgentGatewayAuthenticationException("An Entra access token is required.");
        try
        {
            var token = await acquisition.GetAccessTokenForUserAsync(scopes, authenticationScheme: JwtBearerDefaults.AuthenticationScheme,
                user: context.User, tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
            if (string.IsNullOrWhiteSpace(token))
                throw new AgentGatewayAuthenticationException("A downstream gateway token could not be acquired.", forbidden: true);
            return new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            throw new AgentGatewayAuthenticationException("The caller needs consent or additional authentication for the gateway.", forbidden: true);
        }
    }
}

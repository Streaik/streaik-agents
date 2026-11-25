using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Streaik.Agents.AspNetCore;
using Streaik.Agents.AzureAuth;
using Streaik.Agents.HeaderAuth;

namespace Streaik.Agents.Tests;

internal sealed class TestAgentHost(WebApplication app, HttpClient client, GatewayFixture gateway) : IAsyncDisposable
{
    private const string Issuer = "https://issuer.example.test/tenant/v2.0";
    private const string Audience = "00000000-0000-0000-0000-000000000001";
    internal const string DownstreamScope = "api://test-gateway/access_as_user";
    private static readonly SecurityKey SigningKey = new SymmetricSecurityKey(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    public HttpClient Client { get; } = client;
    public GatewayFixture Gateway { get; } = gateway;

    public static async Task<TestAgentHost> StartAsync(bool azure = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StreaikAgents:GatewayEndpoint"] = "https://gateway.example.test/base",
            ["StreaikAgents:Agents:0:Name"] = "GreetingAgent",
            ["StreaikAgents:Agents:0:Instructions"] = "Reply with a greeting.",
            ["StreaikAgents:Agents:0:Model:Id"] = "openai/test",
            ["StreaikAgents:GatewayScopes:0"] = DownstreamScope,
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000002",
            ["AzureAd:ClientId"] = Audience,
            ["AzureAd:RequiredScope"] = "access_as_user"
        });

        var fixture = new GatewayFixture();
        builder.Services.AddStreaikAgents(builder.Configuration.GetSection("StreaikAgents"));
        builder.Services.AddHttpClient(AgentHosting.GatewayClientName).ConfigurePrimaryHttpMessageHandler(() => fixture);
        if (azure)
        {
            builder.Services.AddAzureAgentAuth(builder.Configuration);
            builder.Services.Replace(ServiceDescriptor.Singleton(_ => DispatchProxy.Create<ITokenAcquisition, TestAcquisition>()));
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Keep real JWT challenge, signature and scope enforcement, but no calls to discovery or MSAL.
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(SigningKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = SigningKey, ValidateIssuerSigningKey = true
                };
            });
        }
        else builder.Services.AddScoped<IAgentGatewayHeaders, HeaderGatewayHeaders>();

        var app = builder.Build();
        if (azure) app.MapAzureAuthAgents();
        else app.MapStreaikAgents();
        await app.StartAsync();
        return new TestAgentHost(app, new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(15)
        }, fixture);
    }

    public static string Token(string user, string scope = "access_as_user") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        Issuer, Audience, [new Claim("sub", user), new Claim("scp", scope)], expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

    public static HttpRequestMessage Post(string path, object body, string? credential = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public class TestAcquisition : DispatchProxy
    {
        protected override object Invoke(MethodInfo? method, object?[]? arguments)
        {
            Assert.Equal(nameof(ITokenAcquisition.GetAccessTokenForUserAsync), method?.Name);
            Assert.Equal(DownstreamScope, Assert.Single(arguments!.OfType<IEnumerable<string>>().Single()));
            var user = Assert.Single(arguments!.OfType<ClaimsPrincipal>());
            var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            Assert.False(string.IsNullOrWhiteSpace(subject));
            return Task.FromResult($"downstream-{subject}");
        }
    }
}

internal sealed class GatewayFixture : HttpMessageHandler
{
    public const string Completed = """
        {"id":"resp_fixture","object":"response","status":"completed","created_at":1690000000,"model":"openai/test",
         "output":[{"id":"msg_fixture","status":"completed","type":"message","role":"assistant","content":[{"type":"output_text","text":"Hello there","annotations":[]}]}],
         "usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}
        """;

    public const string FirstDelta = "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_fixture\",\"delta\":\"Hello\"}\n\n";
    private static readonly string Sse = FirstDelta
        + "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_fixture\",\"delta\":\" there\"}\n\n"
        + $"data: {{\"type\":\"response.completed\",\"response\":{Completed.ReplaceLineEndings("")}}}\n\ndata: [DONE]\n\n";

    public ConcurrentQueue<GatewayCall> Calls { get; } = new();
    public Func<GatewayCall, HttpResponseMessage>? Respond { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var call = new GatewayCall(request.RequestUri!, json.RootElement.Clone(),
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase));
        Calls.Enqueue(call);
        return Respond?.Invoke(call) ?? (call.Body.GetProperty("stream").GetBoolean() ? Reply(Sse, stream: true) : Reply(Completed));
    }

    public static HttpResponseMessage Reply(string content, bool stream = false, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };
}

internal sealed record GatewayCall(Uri Endpoint, JsonElement Body, IReadOnlyDictionary<string, string> Headers);

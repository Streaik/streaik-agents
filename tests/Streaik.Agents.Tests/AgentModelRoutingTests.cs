using System.Net;
using System.Text.Json;

namespace Streaik.Agents.Tests;

public sealed class AgentModelRoutingTests
{
    [Theory]
    [InlineData("openai/second", HttpStatusCode.OK, "openai/second")]
    [InlineData("fireworks/router", HttpStatusCode.OK, "openai/first")]
    [InlineData("openai/second", HttpStatusCode.BadGateway, "openai/first")]
    public async Task Typed_routing_skips_listing_and_confines_choice_and_credentials(string choice, HttpStatusCode decisionStatus, string expected)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => call.Endpoint.AbsolutePath switch
        {
            "/base/api/decisions" => Decision(choice, decisionStatus),
            "/base/v1/responses" => GatewayFixture.Text(call, "Answered"),
            _ => throw new InvalidOperationException("Typed routing must not list models.")
        };
        using var request = TestAgentHost.Post("/v1/responses", JsonSerializer.Deserialize<JsonElement>("""
            {"agent":{"name":"ResearchAgent","instructions":"Reply briefly.","models":[
              {"id":"fireworks/router","type":"decision","providerMetadata":{"strategy":"fast"}},
              {"id":"openai/first","type":"language","options":{"temperature":0.25},"providerMetadata":{"service_tier":"auto"}},
              {"id":"openai/second","type":"language","options":{"temperature":0.75},
               "providerHeaders":{"OpenAI-Beta":"sample=1","x-openai-key":"not-a-caller-key","x-fireworks-key":"wrong-provider"},
               "providerMetadata":{"model":"not-selected","store":true,"tools":[{"type":"mcp"}],"service_tier":"flex","reasoning":{"effort":"low"}}},
              {"id":"images/illust","type":"image"}]},"input":"Find the answer"}
            """), "unscoped-bearer");
        request.Headers.Add("x-openai-key", "answer-key");
        request.Headers.Add("x-fireworks-key", "router-key");
        request.Headers.Add("x-images-key", "unused-key");
        using var result = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Collection(host.Gateway.Calls, call =>
        {
            Assert.Equal("/base/api/decisions", call.Endpoint.AbsolutePath);
            Assert.Equal("fireworks/router", Model(call));
            Assert.Equal("router-key", Assert.Single(Keys(call)).Value);
            Assert.Equal("fast", call.Body.GetProperty("providerOptions").GetProperty("fireworks").GetProperty("strategy").GetString());
            Assert.Equal(new[] { "openai/first", "openai/second" }, call.Body.GetProperty("questions").GetProperty("model")
                .GetProperty("criteria").EnumerateObject().Select(p => p.Name));
        }, call =>
        {
            Assert.Equal("/base/v1/responses", call.Endpoint.AbsolutePath);
            Assert.Equal(expected, Model(call));
            Assert.Equal("answer-key", Assert.Single(Keys(call)).Value);
            Assert.False(call.Body.GetProperty("store").GetBoolean());
            if (expected == "openai/second")
            {
                Assert.Equal("sample=1", call.Headers["OpenAI-Beta"]);
                Assert.Equal(0.75, call.Body.GetProperty("temperature").GetDouble());
                Assert.Equal("flex", call.Body.GetProperty("service_tier").GetString());
                Assert.Equal("low", call.Body.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.False(call.Body.TryGetProperty("tools", out _));
            }
            else
            {
                Assert.Equal(0.25, call.Body.GetProperty("temperature").GetDouble());
                Assert.Equal("auto", call.Body.GetProperty("service_tier").GetString());
            }
        });
        Assert.All(host.Gateway.Calls, call => Assert.False(call.Headers.ContainsKey("Authorization")));
    }

    [Theory]
    [InlineData("/v1/responses", false)]
    [InlineData("/api/chat", true)]
    public async Task Explicit_router_and_answer_can_share_an_id(string endpoint, bool decisionFirst)
    {
        const string router = """{"id":"openai/shared","type":"decision","providerMetadata":{"strategy":"decision-only"}}""";
        const string answer = """{"id":"openai/shared","type":"language","options":{"temperature":0.25},"providerMetadata":{"service_tier":"flex"}}""";
        const string alternate = """{"id":"openai/alternate","type":"language"}""";
        var models = decisionFirst ? new[] { router, answer, alternate } : new[] { answer, router, alternate };
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => call.Endpoint.AbsolutePath switch
        {
            "/base/api/decisions" => Decision("openai/shared"),
            "/base/v1/responses" => GatewayFixture.Text(call, "Answered"),
            _ => throw new InvalidOperationException("An explicit router and typed answers do not require discovery.")
        };
        using var request = RoutedRequest(endpoint, string.Join(',', models));
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Answered", await response.Content.ReadAsStringAsync());
        Assert.Collection(host.Gateway.Calls, call =>
        {
            Assert.Equal("/base/api/decisions", call.Endpoint.AbsolutePath);
            Assert.Equal("openai/shared", Model(call));
            Assert.Equal("decision-only", call.Body.GetProperty("providerOptions").GetProperty("openai").GetProperty("strategy").GetString());
            Assert.Equal(new[] { "openai/shared", "openai/alternate" }, Criteria(call).EnumerateObject().Select(p => p.Name));
        }, call =>
        {
            Assert.Equal("/base/v1/responses", call.Endpoint.AbsolutePath);
            Assert.Equal("openai/shared", Model(call));
            Assert.Equal(0.25, call.Body.GetProperty("temperature").GetDouble());
            Assert.Equal("flex", call.Body.GetProperty("service_tier").GetString());
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Catalog_dual_use_models_keep_both_roles_regardless_of_listing_order(bool typed, bool decisionFirst)
    {
        const string answer = """{"id":"openai/shared","type":"language","description":"Answer capabilities"}""";
        const string decision = """{"id":"openai/shared","type":"decision","description":"Router capabilities"}""";
        var listing = decisionFirst ? $"{decision},{answer}" : $"{answer},{decision}";
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => call.Endpoint.AbsolutePath switch
        {
            "/base/v1/models" => GatewayFixture.Reply($$"""{"data":[{{listing}},{"id":"openai/alternate","type":"language"}]}"""),
            "/base/api/decisions" => Decision("openai/alternate"),
            "/base/v1/responses" => GatewayFixture.Text(call, "Answered"),
            _ => throw new InvalidOperationException("Unexpected gateway call.")
        };
        var models = typed ? """{"id":"openai/shared","type":"language"},{"id":"openai/alternate","type":"language"}"""
            : """{"id":"openai/shared"},{"id":"openai/alternate"}""";
        using var request = RoutedRequest("/v1/responses", models);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Collection(host.Gateway.Calls, call => Assert.Equal("/base/v1/models", call.Endpoint.AbsolutePath), call =>
        {
            Assert.Equal("/base/api/decisions", call.Endpoint.AbsolutePath);
            Assert.Equal("openai/shared", Model(call));
            var criteria = Criteria(call);
            Assert.Equal(new[] { "openai/shared", "openai/alternate" }, criteria.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Answer capabilities", criteria.GetProperty("openai/shared").GetProperty("description").GetString());
        }, call =>
        {
            Assert.Equal("/base/v1/responses", call.Endpoint.AbsolutePath);
            Assert.Equal("openai/alternate", Model(call));
        });
    }

    [Fact]
    public async Task Gateway_catalog_is_shared_within_each_workflow_but_never_across_callers()
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => call.Endpoint.AbsolutePath switch
        {
            "/base/v1/models" => GatewayFixture.Reply("""
                {"data":[null,{"id":"openai/router","type":"decision"},{"id":"openai/first","type":"language"},
                    {"id":"openai/second","type":"language","description":"Larger context","agent":{"instructions":"not routing evidence"}},
                    {"id":"openai/third","type":"language"}]}
                """),
            "/base/api/decisions" => Decision("openai/second"),
            _ => GatewayFixture.Text(call, $"Answered by {Model(call)}")
        };
        var body = JsonSerializer.Deserialize<JsonElement>("""
            {"agents":[
              {"name":"Draft","models":[{"id":"openai/router"},{"id":"openai/first"},{"id":"openai/second"}]},
              {"name":"Review","models":[{"id":"openai/third"},{"id":"openai/first"}]}
            ],"workflowType":"groupchat","workflowMetadata":{"groupchat":{"maximumIterationCount":4}},
            "messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Draft and review"}]}]}
            """);
        foreach (var key in new[] { "first-caller", "next-caller" })
        {
            using var request = TestAgentHost.Post("/api/chat", body, key);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Answered by openai/second", await response.Content.ReadAsStringAsync());
            var calls = host.Gateway.Calls.Where(call => call.Headers.GetValueOrDefault("x-openai-key") == key).ToArray();
            Assert.Equal("/base/v1/models", calls[0].Endpoint.AbsolutePath);
            Assert.Single(calls, call => call.Endpoint.AbsolutePath == "/base/v1/models");
            var decision = Assert.Single(calls, call => call.Endpoint.AbsolutePath == "/base/api/decisions");
            var metadata = decision.Body.GetProperty("questions").GetProperty("model").GetProperty("criteria").GetProperty("openai/second");
            Assert.Equal("Larger context", metadata.GetProperty("description").GetString());
            Assert.False(metadata.TryGetProperty("agent", out _));
            Assert.Equal(new[] { "openai/second", "openai/third", "openai/second", "openai/third" },
                calls.Where(call => call.Endpoint.AbsolutePath == "/base/v1/responses").Select(Model));
        }
    }

    [Theory]
    [InlineData("""[{"id":"openai/answer"},{"id":"openai/answer"}]""")]
    [InlineData("""[{"id":"fireworks/router","type":"decision"},{"id":"openai/answer","type":"language"}]""")]
    [InlineData("""[{"id":"openai/answer","type":"language"},{"id":"other/transcriber","type":"transcription"}]""")]
    [InlineData("""[{"id":"openai/answer","type":"decision"},{"id":"openai/answer","type":"language"}]""")]
    public async Task A_single_eligible_answer_does_not_need_a_decision_or_list(string models)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = TestAgentHost.Post("/v1/responses", JsonSerializer.Deserialize<JsonElement>(
            $$"""{"agent":{"name":"Inline","models":{{models}}},"input":"Hi"}"""), "answer-key");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("openai/answer", Model(Assert.Single(host.Gateway.Calls)));
    }

    private static HttpRequestMessage RoutedRequest(string endpoint, string models)
    {
        var body = endpoint == "/api/chat"
            ? $$"""{"agent":{"name":"RoutingAgent","models":[{{models}}]},"messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Find the answer"}]}]}"""
            : $$"""{"agent":{"name":"RoutingAgent","models":[{{models}}]},"input":"Find the answer"}""";
        return TestAgentHost.Post(endpoint, JsonSerializer.Deserialize<JsonElement>(body), "answer-key");
    }

    private static string? Model(GatewayCall call) => call.Body.GetProperty("model").GetString();

    private static JsonElement Criteria(GatewayCall call) => call.Body.GetProperty("questions").GetProperty("model").GetProperty("criteria");

    private static IEnumerable<KeyValuePair<string, string>> Keys(GatewayCall call) => call.Headers
        .Where(header => header.Key.StartsWith("x-", StringComparison.OrdinalIgnoreCase) && header.Key.EndsWith("-key", StringComparison.OrdinalIgnoreCase));

    private static HttpResponseMessage Decision(string choice, HttpStatusCode status = HttpStatusCode.OK) => GatewayFixture.Reply(
        JsonSerializer.Serialize(new { answers = new { model = new { type = "choice", choice } } }), status: status);
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Streaik.Agents.Tests;

public sealed class WorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sequential_passes_agent_output_to_the_next_agent(bool stream)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => GatewayFixture.Text(call, call.Body.GetProperty("model").GetString() == "openai/test" ? "Draft" : "Reviewed");
        using var request = TestAgentHost.Post("/v1/responses", new
        {
            models = new[] { "GreetingAgent", "ReviewAgent" }, input = "Say hello", stream, max_output_tokens = 64
        }, "same-provider-key");
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("Reviewed", body);
        if (stream) Assert.Contains("response.completed", body);
        var calls = host.Gateway.Calls.ToArray();
        Assert.Equal(new[] { "openai/test", "openai/review" }, calls.Select(call => call.Body.GetProperty("model").GetString()));
        Assert.Contains("Draft", calls[1].Body.GetProperty("input").GetRawText());
        Assert.All(calls, call =>
        {
            Assert.Equal(64, call.Body.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("same-provider-key", Assert.Single(Credentials(call)).Value);
            Assert.False(call.Body.TryGetProperty("models", out _));
            Assert.False(call.Body.TryGetProperty("workflow_type", out _));
        });
    }

    [Fact]
    public async Task Groupchat_honors_iteration_count_on_the_ui_route()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = TestAgentHost.Post("/api/chat", new
        {
            models = new[] { "GreetingAgent", "ReviewAgent" }, workflowType = "groupchat",
            workflowMetadata = new { groupchat = new { maximumIterationCount = 2 } },
            messages = new[] { new { id = "user-1", role = "user", parts = new[] { new { type = "text", text = "Hello" } } } }
        }, "test-key");
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("v1", response.Headers.GetValues("x-vercel-ai-ui-message-stream").Single());
        Assert.Contains("text-delta", body);
        Assert.DoesNotContain("\"type\":\"error\"", body);
        Assert.Contains("\"type\":\"finish\"", body);
        Assert.Equal(new[] { "openai/test", "openai/review" }, host.Gateway.Calls.Select(call => call.Body.GetProperty("model").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_isolates_mixed_provider_credentials(bool stream)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call =>
        {
            var completed = JsonNode.Parse(GatewayFixture.Completed)!;
            completed["metadata"] = JsonNode.Parse("""{"gateway":{"cost":0.05}}""");
            return GatewayFixture.ReplyResult(call, completed.ToJsonString());
        };
        var body = new
        {
            models = new[] { "GreetingAgent" }, agent = new { name = "Other", model = new { id = "anthropic/test" } },
            workflow_type = "concurrent", input = "Hi", stream
        };
        using (var missing = TestAgentHost.Post("/v1/responses", body, "never-fan-out"))
        {
            using var rejected = await host.Client.SendAsync(missing);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Empty(host.Gateway.Calls);
        }
        using var request = TestAgentHost.Post("/v1/responses", body, "never-fan-out");
        request.Headers.Add("x-openai-key", "openai-only");
        request.Headers.Add("x-anthropic-key", "anthropic-only");
        using var response = await host.Client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, result);
        Assert.Equal(2, host.Gateway.Calls.Count);
        Assert.All(host.Gateway.Calls, call =>
        {
            var provider = call.Body.GetProperty("model").GetString()!.Split('/')[0];
            Assert.Equal(new KeyValuePair<string, string>($"x-{provider}-key", $"{provider}-only"), Assert.Single(Credentials(call)));
        });
        var completed = stream ? SseEvents(result).Single(item => item["type"]?.GetValue<string>() == "response.completed")["response"] : JsonNode.Parse(result);
        Assert.Equal(14, completed!["usage"]!["total_tokens"]!.GetValue<int>());
        Assert.Equal(0.10m, (decimal?)completed["metadata"]!["gateway"]!["cost"]);
        Assert.Null(completed["metadata"]!["gateway"]!["tps"]); // A concurrent workflow releases one aggregated delta.
    }

    [Fact]
    public async Task A_later_agent_failure_ends_the_workflow_stream_without_leaking_the_gateway_error()
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => call.Body.GetProperty("model").GetString() == "openai/test"
            ? GatewayFixture.Text(call, "Draft ready")
            : GatewayFixture.Reply("private failure detail", status: HttpStatusCode.TooManyRequests);
        using var request = TestAgentHost.Post("/v1/responses", new
        {
            models = new[] { "GreetingAgent", "ReviewAgent" }, input = "Say hello", stream = true
        }, "test-key");
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("Draft ready", body);
        Assert.Contains("response.failed", body);
        Assert.DoesNotContain("response.completed", body);
        Assert.DoesNotContain("private failure detail", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handoff_uses_a_framework_function_call_and_stops_at_the_next_user_turn(bool stream)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call =>
        {
            if (call.Body.GetProperty("model").GetString() == "openai/review") return GatewayFixture.Text(call, "Reviewed after handoff");
            var tool = Assert.Single(call.Body.GetProperty("tools").EnumerateArray());
            return GatewayFixture.Complete(call, [new { type = "function_call", id = "fc_transfer", call_id = "transfer", name = tool.GetProperty("name").GetString(), arguments = "{}" }]);
        };
        using var request = TestAgentHost.Post("/v1/responses", new
        {
            models = new[] { "GreetingAgent", "ReviewAgent" }, workflow_type = "handoff", input = "Hi", stream,
            workflow_metadata = new { handoff = new { handoffs = new object[][] { ["GreetingAgent", new[] { "ReviewAgent" }] } } }
        }, "test-key");
        using var response = await host.Client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, result + string.Join("\n", host.Gateway.Calls.Select(call => call.Body)));
        Assert.Contains("Reviewed after handoff", result);
        Assert.DoesNotContain("function_call", result);
        if (stream) Assert.Contains("response.completed", result);
        Assert.Equal(2, host.Gateway.Calls.Count);
    }

    [Fact]
    public async Task Magentic_runs_the_manager_and_participant_without_exposing_the_plan()
    {
        await using var host = await TestAgentHost.StartAsync();
        var ledgers = 0;
        host.Gateway.Respond = call =>
        {
            if (call.Body.GetProperty("model").GetString() == "openai/review") return GatewayFixture.Text(call, "Participant reviewed the draft.");
            if (call.Body.GetProperty("input").EnumerateArray().Last().GetRawText().Contains("is_request_satisfied", StringComparison.Ordinal))
            {
                var satisfied = Interlocked.Increment(ref ledgers) > 1;
                return GatewayFixture.Text(call, JsonSerializer.Serialize(new
                {
                    is_request_satisfied = new { reason = "Fixture", answer = satisfied },
                    is_in_loop = new { reason = "Fixture", answer = false },
                    is_progress_being_made = new { reason = "Fixture", answer = true },
                    next_speaker = new { reason = "Fixture", answer = "ReviewAgent" },
                    instruction_or_question = new { reason = "Fixture", answer = "Review the draft." }
                }));
            }
            return GatewayFixture.Text(call, ledgers == 0 ? "Private planning text" : "Magentic final answer");
        };
        using var request = TestAgentHost.Post("/v1/responses", new
        {
            models = new[] { "GreetingAgent", "ReviewAgent" }, workflow_type = "magentic", input = "Ask the team to review this draft.", stream = true,
            workflow_metadata = new { magentic = new { maxRounds = 4 } }
        }, "test-key");
        using var response = await host.Client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, result + string.Join("\n", host.Gateway.Calls.Select(call => call.Body)));
        Assert.True(ledgers == 2, $"Expected two progress ledgers, got {ledgers}. {result}\n" + string.Join("\n", host.Gateway.Calls.Select(call => call.Body.GetProperty("input").EnumerateArray().Last().GetRawText())));
        Assert.Single(host.Gateway.Calls, call => call.Body.GetProperty("model").GetString() == "openai/review");
        Assert.Contains("Magentic final answer", result);
        Assert.DoesNotContain("Private planning text", result);
        Assert.DoesNotContain("is_request_satisfied", result);
        Assert.Contains("response.completed", result);
    }

    [Theory]
    [InlineData("agent-1", HttpStatusCode.OK)]
    [InlineData("not-offered", HttpStatusCode.BadGateway)]
    public async Task Decisions_routes_once_and_only_runs_an_offered_agent(string choice, HttpStatusCode expected)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => call.Endpoint.AbsolutePath.EndsWith("/api/decisions")
            ? GatewayFixture.Reply(JsonSerializer.Serialize(new { answers = new { agent = new { type = "choice", choice } } }))
            : GatewayFixture.Text(call, "Selected agent responded");
        using var request = TestAgentHost.Post("/v1/responses", new
        {
            models = new[] { "GreetingAgent" }, agent = new { name = "Unused", instructions = "private candidate instructions", model = new { id = "anthropic/unused" } },
            workflow_type = "decisions", workflow_metadata = new { decisions = new { model = "fireworks/router" } }, input = "Hi"
        }, "never-fan-out");
        request.Headers.Add("x-fireworks-key", "router-only");
        request.Headers.Add("x-openai-key", "selected-only");
        using var response = await host.Client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, result);
        var calls = host.Gateway.Calls.ToArray();
        Assert.EndsWith("/base/api/decisions", calls[0].Endpoint.AbsolutePath);
        Assert.Equal("router-only", Assert.Single(Credentials(calls[0])).Value);
        Assert.DoesNotContain("private candidate instructions", calls[0].Body.GetRawText());
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(2, calls.Length);
            Assert.Equal("openai/test", calls[1].Body.GetProperty("model").GetString());
            Assert.Equal("selected-only", Assert.Single(Credentials(calls[1])).Value);
            Assert.Contains("Selected agent responded", result);
        }
        else Assert.Single(calls);
    }

    [Theory]
    [InlineData("""{"models":["GreetingAgent","Missing"],"input":"Hi"}""", HttpStatusCode.NotFound)]
    [InlineData("""{"models":["GreetingAgent","greetingagent"],"input":"Hi"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"models":["GreetingAgent","ReviewAgent"],"workflow_type":"handoff","workflow_metadata":{"handoff":{"handoffs":[["GreetingAgent",["Missing"]]]}},"input":"Hi"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"models":["GreetingAgent","ReviewAgent"],"workflow_type":"decisions","input":"Hi"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"models":["GreetingAgent","ReviewAgent"],"tools":[{"type":"function"}],"input":"Hi"}""", HttpStatusCode.BadRequest)]
    public async Task Invalid_workflows_never_call_the_gateway(string body, HttpStatusCode status)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var response = await host.Client.PostAsync("/v1/responses", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, response.StatusCode);
        Assert.Empty(host.Gateway.Calls);
    }

    [Theory]
    [InlineData("/v1/responses", "workflow_type", "workflowType")]
    [InlineData("/api/chat", "workflowType", "workflow_type")]
    public async Task Alternate_workflow_type_is_rejected_with_the_correct_spelling(string path, string expected, string alternate)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = JsonNode.Parse(path == "/api/chat"
            ? """{"model":"GreetingAgent","messages":[{"id":"1","role":"user","parts":[{"type":"text","text":"Hi"}]}]}"""
            : """{"model":"GreetingAgent","input":"Hi"}""")!.AsObject();
        body[alternate] = "concurrent";
        using var request = TestAgentHost.Post(path, body, "test-key");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!;
        Assert.Equal($"Use '{expected}' on this endpoint, not '{alternate}'.", error["message"]!.GetValue<string>());
        Assert.Empty(host.Gateway.Calls);
    }

    private static IEnumerable<KeyValuePair<string, string>> Credentials(GatewayCall call) => call.Headers.Where(header =>
        header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.Key.EndsWith("-key", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<JsonNode> SseEvents(string body) => body.Split('\n')
        .Where(line => line.StartsWith("data: {", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[6..])!);
}

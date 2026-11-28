using System.Text.Json.Nodes;

namespace Streaik.Agents.Tests;

public sealed class AgentAccountingTests
{
    private const string Chat = "/api/chat";
    private const string Responses = "/v1/responses";

    [Theory]
    [InlineData(Chat, true)]
    [InlineData(Responses, true)]
    [InlineData(Responses, false)]
    public async Task Sums_decision_cost_without_decision_usage_and_measures_only_agent_timing(string endpoint, bool stream)
    {
        var clock = new StepClock();
        await using var host = await TestAgentHost.StartAsync(clock: clock);
        var completed = JsonNode.Parse(GatewayFixture.Completed)!;
        completed["metadata"] = JsonNode.Parse("""{"gateway":{"cost":"0.125","ttft":999,"tps":999},"unrelated":true}""");
        completed["usage"]!["input_tokens_details"] = JsonNode.Parse("""{"cached_tokens":2,"cache_write_tokens":1}""");
        completed["usage"]!["output_tokens_details"] = JsonNode.Parse("""{"reasoning_tokens":1}""");
        host.Gateway.Respond = call =>
        {
            if (call.Endpoint.AbsolutePath.EndsWith("/api/decisions", StringComparison.Ordinal))
            {
                clock.GetTimestamp(); // Simulate time spent at each routing step before the answer stream starts.
                var agentChoice = call.Body.GetProperty("questions").TryGetProperty("agent", out _);
                var (question, choice, cost) = agentChoice ? ("agent", "agent-0", "0.25") : ("model", "openai/second", "0.5");
                return GatewayFixture.Reply($$$"""
                    {"answers":{"{{{question}}}":{"type":"choice","choice":"{{{choice}}}"}},
                     "usage":{"promptTokens":100,"completionTokens":200,"totalTokens":300},
                     "providerMetadata":{"gateway":{"cost":{{{cost}}}},"other":{"used":true}} }
                    """);
            }
            Assert.Equal("openai/second", call.Body.GetProperty("model").GetString());
            var result = completed.ToJsonString();
            // Only the completed result counts, even if an earlier frame carries the same totals.
            var prefix = $"data: {{\"type\":\"response.created\",\"response\":{result}}}\n\n" + GatewayFixture.FirstDelta
                + "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_fixture\",\"delta\":\" there\"}\n\n";
            return GatewayFixture.ReplyResult(call, result, prefix);
        };

        var chat = endpoint == Chat;
        var body = RequestBody(endpoint, stream);
        body.Remove("model");
        body["agents"] = JsonNode.Parse("""
            [{"name":"Selected","models":[{"id":"openai/model-router","type":"decision"},
              {"id":"openai/first","type":"language"},{"id":"openai/second","type":"language"}]},
             {"name":"Unused","model":{"id":"openai/unused"}}]
            """);
        body[chat ? "workflowType" : "workflow_type"] = "decisions";
        body[chat ? "workflowMetadata" : "workflow_metadata"] = JsonNode.Parse("""{"decisions":{"model":"openai/agent-router"}}""");
        using var request = TestAgentHost.Post(endpoint, body, "answer-key");
        using var response = await host.Client.SendAsync(request);
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);
        Assert.Equal(3, host.Gateway.Calls.Count);
        var terminal = Terminal(wire, chat, stream);
        Assert.Equal("Selected", (string?)terminal["model"]);
        var usage = terminal["usage"]!;
        Assert.Equal(5, (int?)usage[chat ? "promptTokens" : "input_tokens"]);
        Assert.Equal(2, (int?)usage[chat ? "completionTokens" : "output_tokens"]);
        Assert.Equal(7, (int?)usage[chat ? "totalTokens" : "total_tokens"]);
        if (!chat)
        {
            Assert.Equal(2, (int?)usage["input_tokens_details"]!["cached_tokens"]);
            Assert.Equal(1, (int?)usage["input_tokens_details"]!["cache_write_tokens"]);
            Assert.Equal(1, (int?)usage["output_tokens_details"]!["reasoning_tokens"]);
        }
        var gateway = Assert.IsType<JsonObject>(Assert.Single(terminal[chat ? "providerMetadata" : "metadata"]!.AsObject()).Value);
        Assert.Equal(0.875m, (decimal?)gateway["cost"]);
        Assert.Equal(stream ? 6d : null, (double?)gateway["ttft"]);
        Assert.Equal(stream ? 1d : null, (double?)gateway["tps"]);
        Assert.Equal(stream ? 3 : 1, gateway.Count);
    }

    [Theory]
    [InlineData(Chat, "0", false)]
    [InlineData(Responses, "0", false)]
    [InlineData(Chat, "null", true)]
    [InlineData(Responses, "\"not-a-cost\"", true)]
    public async Task Keeps_zero_but_omits_unavailable_cost_and_does_not_invent_throughput(string endpoint, string cost, bool withText)
    {
        await using var host = await TestAgentHost.StartAsync(clock: new StepClock());
        var completed = JsonNode.Parse(GatewayFixture.Completed)!;
        completed["metadata"] = JsonNode.Parse($$$"""{"gateway":{"cost":{{{cost}}},"ttft":999,"tps":999}}""");
        completed["usage"] = JsonNode.Parse("""{"cost":123}"""); // Not a token count or a gateway price.
        if (!withText) completed["output"] = new JsonArray();
        host.Gateway.Respond = call => GatewayFixture.ReplyResult(call, completed.ToJsonString());
        using var request = TestAgentHost.Post(endpoint, RequestBody(endpoint, stream: true), "answer-key");
        using var response = await host.Client.SendAsync(request);
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);
        var chat = endpoint == Chat;
        var terminal = Terminal(wire, chat, stream: true);
        Assert.Null(terminal["usage"]?[chat ? "totalTokens" : "total_tokens"]);
        var gateway = terminal[chat ? "providerMetadata" : "metadata"]!["gateway"]!;
        Assert.Equal(withText ? null : 0m, (decimal?)gateway["cost"]);
        Assert.Equal(withText ? 2d : null, (double?)gateway["ttft"]);
        Assert.Null(gateway["tps"]);
    }

    private static JsonObject RequestBody(string endpoint, bool stream) => endpoint == Chat
        ? JsonNode.Parse("""{"model":"GreetingAgent","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hello"}]}]}""")!.AsObject()
        : new() { ["model"] = "GreetingAgent", ["input"] = "Hello", ["stream"] = stream };

    internal static JsonNode Terminal(string wire, bool chat, bool stream) => stream
        ? Assert.Single(wire.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!), frame => (string?)frame["type"] == (chat ? "finish" : "response.completed"))
            [chat ? "messageMetadata" : "response"]!
        : JsonNode.Parse(wire)!;

    private sealed class StepClock : TimeProvider
    {
        private long _tick;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _tick, 2 * TimeSpan.TicksPerSecond);
    }
}

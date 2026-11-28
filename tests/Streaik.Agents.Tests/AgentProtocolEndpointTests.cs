using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Streaik.Agents.Tests;

public sealed class AgentProtocolEndpointTests
{
    private const string UserMessage = """{"role":"user","content":"Say hi"}""";
    private const string WeatherArgs = """{"city":"Amsterdam"}""";
    private const string CallId = "call_weather";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Json_preserves_agent_name_instructions_and_gateway_routing(bool chat)
    {
        await using var host = await TestAgentHost.StartAsync();
        var messages = chat ? """{"role":"developer","content":[{"type":"text","text":"Be brief."}]},""" + UserMessage : UserMessage;
        var options = chat ? """, "max_completion_tokens":64, "store":false, "temperature":0.5, "top_p":0.8"""
            : """, "system":[{"type":"text","text":"Be brief."}], "temperature":0.5, "top_p":0.8""";
        using var reply = await SendAsync(host, chat, Request(chat, messages, options, model: "greetingagent"));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var json = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("GreetingAgent", json.GetProperty("model").GetString());
        Assert.StartsWith(chat ? "chatcmpl-" : "msg_", json.GetProperty("id").GetString());
        var usage = json.GetProperty("usage");
        Assert.Equal(5, usage.GetProperty(chat ? "prompt_tokens" : "input_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty(chat ? "completion_tokens" : "output_tokens").GetInt32());
        if (chat)
        {
            Assert.Equal("chat.completion", json.GetProperty("object").GetString());
            var choice = Assert.Single(json.GetProperty("choices").EnumerateArray());
            Assert.Equal(0, choice.GetProperty("index").GetInt32());
            Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
            Assert.Equal("Hello there", choice.GetProperty("message").GetProperty("content").GetString());
        }
        else
        {
            Assert.Equal("message", json.GetProperty("type").GetString());
            Assert.Equal("end_turn", json.GetProperty("stop_reason").GetString());
            Assert.Equal("Hello there", json.GetProperty("content")[0].GetProperty("text").GetString());
        }
        var call = Assert.Single(host.Gateway.Calls);
        Assert.Equal("/base/v1/responses", call.Endpoint.AbsolutePath);
        Assert.Equal("openai/test", call.Body.GetProperty("model").GetString());
        Assert.Equal("fixture-key", call.Headers["x-openai-key"]);
        Assert.DoesNotContain("Authorization", call.Headers.Keys);
        Assert.Contains("Reply with a greeting.", call.Body.GetRawText());
        Assert.Contains("Be brief.", call.Body.GetRawText());
        Assert.False(call.Body.GetProperty("store").GetBoolean());
        Assert.Equal(chat ? 64 : 128, call.Body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.5, call.Body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, call.Body.GetProperty("top_p").GetDouble(), precision: 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streams_follow_native_event_order_and_usage_contracts(bool chat)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var reply = await SendAsync(host, chat, Request(chat, options: chat ? """, "stream":true, "stream_options":{"include_usage":true}""" : """, "stream":true"""));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("text/event-stream", reply.Content.Headers.ContentType?.MediaType);
        var body = await reply.Content.ReadAsStringAsync();
        var events = Frames(body);
        if (chat)
        {
            Assert.EndsWith("data: [DONE]\n\n", body);
            Assert.Equal(5, events.Length);
            Assert.All(events, chunk =>
            {
                Assert.Equal("chat.completion.chunk", chunk.GetProperty("object").GetString());
                Assert.Equal("GreetingAgent", chunk.GetProperty("model").GetString());
                Assert.Equal(events[0].GetProperty("id").GetString(), chunk.GetProperty("id").GetString());
                Assert.Equal(events[0].GetProperty("created").GetInt64(), chunk.GetProperty("created").GetInt64());
            });
            Assert.Equal("assistant", events[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
            Assert.Equal(JsonValueKind.Null, events[0].GetProperty("choices")[0].GetProperty("finish_reason").ValueKind);
            Assert.Equal("Hello there", string.Concat(events[1..3].Select(chunk => chunk.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString())));
            Assert.Equal("stop", events[3].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
            Assert.Empty(events[^1].GetProperty("choices").EnumerateArray());
            Assert.Equal(7, events[^1].GetProperty("usage").GetProperty("total_tokens").GetInt32());
        }
        else
        {
            var expected = new[] { "message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop" };
            Assert.Equal(expected, events.Select(evt => evt.GetProperty("type").GetString()));
            Assert.Equal(expected.Select(type => $"event: {type}"), body.Split('\n').Where(line => line.StartsWith("event: ", StringComparison.Ordinal)));
            Assert.DoesNotContain("[DONE]", body);
            var start = events[0].GetProperty("message");
            Assert.Equal("GreetingAgent", start.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Null, start.GetProperty("stop_reason").ValueKind);
            Assert.Equal("Hello there", string.Concat(events[2..4].Select(evt => evt.GetProperty("delta").GetProperty("text").GetString())));
            Assert.Equal("end_turn", events[^2].GetProperty("delta").GetProperty("stop_reason").GetString());
            Assert.Equal(5, events[^2].GetProperty("usage").GetProperty("input_tokens").GetInt32());
            Assert.Equal(2, events[^2].GetProperty("usage").GetProperty("output_tokens").GetInt32());
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Caller_executes_function_and_replays_correlated_result(bool chat, bool stream)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = call => GatewayFixture.Complete(call,
            [new { type = "function_call", id = "fc_fixture", call_id = CallId, name = "weather", arguments = WeatherArgs }]);
        var tools = chat
            ? """, "tools":[{"type":"function","function":{"name":"weather","parameters":{"type":"object"}}}]"""
            : """, "tools":[{"name":"weather","input_schema":{"type":"object"}}]""";
        var choice = chat
            ? """, "tool_choice":{"type":"function","function":{"name":"weather"}}, "parallel_tool_calls":false"""
            : """, "tool_choice":{"type":"tool","name":"weather","disable_parallel_tool_use":true}""";
        using var first = await SendAsync(host, chat, Request(chat, options: tools + choice + (stream ? """, "stream":true""" : "")));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        var wire = stream ? Frames(body) : [Parse(body)];
        string id;
        if (chat)
        {
            var choices = wire.SelectMany(item => item.GetProperty("choices").EnumerateArray()).ToArray();
            var emitted = stream ? choices.Select(item => item.GetProperty("delta")).Single(delta => delta.TryGetProperty("tool_calls", out _))
                : Assert.Single(choices).GetProperty("message");
            var tool = Assert.Single(emitted.GetProperty("tool_calls").EnumerateArray());
            Assert.Equal("weather", tool.GetProperty("function").GetProperty("name").GetString());
            Assert.Equal(WeatherArgs, tool.GetProperty("function").GetProperty("arguments").GetString());
            if (stream)
            {
                Assert.Equal(0, tool.GetProperty("index").GetInt32());
                Assert.All(wire, chunk => Assert.False(chunk.TryGetProperty("usage", out _)));
            }
            else Assert.Equal(JsonValueKind.Null, emitted.GetProperty("content").ValueKind);
            Assert.Equal("tool_calls", choices[^1].GetProperty("finish_reason").GetString());
            id = tool.GetProperty("id").GetString()!;
        }
        else
        {
            var tool = stream ? wire[1].GetProperty("content_block") : wire[0].GetProperty("content")[0];
            Assert.Equal("tool_use", tool.GetProperty("type").GetString());
            Assert.Equal("weather", tool.GetProperty("name").GetString());
            if (stream)
            {
                Assert.Equal("{}", tool.GetProperty("input").GetRawText());
                Assert.Equal("input_json_delta", wire[2].GetProperty("delta").GetProperty("type").GetString());
                Assert.Equal(WeatherArgs, wire[2].GetProperty("delta").GetProperty("partial_json").GetString());
                Assert.Equal("tool_use", wire[^2].GetProperty("delta").GetProperty("stop_reason").GetString());
                Assert.All(wire[1..4], evt => Assert.Equal(0, evt.GetProperty("index").GetInt32()));
            }
            else
            {
                Assert.Equal(WeatherArgs, tool.GetProperty("input").GetRawText());
                Assert.Equal("tool_use", wire[0].GetProperty("stop_reason").GetString());
            }
            id = tool.GetProperty("id").GetString()!;
        }
        Assert.Equal(CallId, id);
        var gateway = Assert.Single(host.Gateway.Calls); // The host did not invoke the caller's function or run a second round.
        Assert.Equal("weather", gateway.Body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("weather", gateway.Body.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.False(gateway.Body.GetProperty("parallel_tool_calls").GetBoolean());

        host.Gateway.Respond = call => GatewayFixture.Text(call, "It is sunny.");
        var history = UserMessage + (chat
            ? $$$"""
                ,{"role":"assistant","content":null,"tool_calls":[{"id":"{{{id}}}","type":"function","function":{"name":"weather","arguments":{{{JsonSerializer.Serialize(WeatherArgs)}}}}}]},{"role":"tool","tool_call_id":"{{{id}}}","content":"sunny"}
                """
            : $$$"""
                ,{"role":"assistant","content":[{"type":"tool_use","id":"{{{id}}}","name":"weather","input":{{{WeatherArgs}}}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{id}}}","content":[{"type":"text","text":"sunny"}]},{"type":"text","text":"Summarize it."}]}
                """);
        using var second = await SendAsync(host, chat, Request(chat, history, tools));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("It is sunny.", await second.Content.ReadAsStringAsync());
        Assert.Equal(2, host.Gateway.Calls.Count);
        var replay = host.Gateway.Calls.Last().Body.GetProperty("input").EnumerateArray().ToArray();
        var callItem = Assert.Single(replay.Where(item => item.GetProperty("type").GetString() == "function_call"));
        Assert.Equal(id, callItem.GetProperty("call_id").GetString());
        Assert.Equal(WeatherArgs, callItem.GetProperty("arguments").GetString());
        var result = Assert.Single(replay.Where(item => item.GetProperty("type").GetString() == "function_call_output"));
        Assert.Equal(id, result.GetProperty("call_id").GetString());
        Assert.Equal("sunny", result.GetProperty("output").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Upstream_output_limit_returns_partial_text_with_native_finish_reason(bool chat, bool stream)
    {
        await using var host = await TestAgentHost.StartAsync();
        var incomplete = GatewayFixture.Completed.Replace("\"status\":\"completed\"", "\"status\":\"incomplete\"", StringComparison.Ordinal)
            .Replace("\"output\":", "\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":", StringComparison.Ordinal);
        host.Gateway.Respond = _ => stream ? GatewayFixture.Reply($"data: {{\"type\":\"response.incomplete\",\"response\":{incomplete.ReplaceLineEndings("")}}}\n\n", stream: true)
            : GatewayFixture.Reply(incomplete);
        using var reply = await SendAsync(host, chat, Request(chat, options: stream ? """, "stream":true""" : ""));
        var body = await reply.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("Hello there", body);
        Assert.Contains(chat ? "\"finish_reason\":\"length\"" : "\"stop_reason\":\"max_tokens\"", body);
    }

    [Theory]
    [InlineData(true, """{"model":"GreetingAgent","messages":[],"n":2}""")]
    [InlineData(true, """{"model":"GreetingAgent","messages":[{"role":"user","content":"hi"}],"store":true}""")]
    [InlineData(true, """{"model":"GreetingAgent","messages":[{"role":"user","content":"hi"},{"role":"tool","tool_call_id":"missing","content":"data"}]}""")]
    [InlineData(false, """{"model":"GreetingAgent","messages":[{"role":"user","content":"hi"}]}""")]
    [InlineData(false, """{"model":"GreetingAgent","max_tokens":128,"messages":[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://example.test/a.png"}}]}]}""")]
    [InlineData(false, """{"model":"GreetingAgent","max_tokens":128,"messages":[{"role":"user","content":"hi"}],"tools":[{"name":"streaik_internal","input_schema":{"type":"object"}}]}""")]
    public async Task Invalid_requests_fail_without_calling_gateway(bool chat, string request)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var reply = await SendAsync(host, chat, request);
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        var json = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request_error", json.GetProperty("error").GetProperty("type").GetString());
        if (!chat) Assert.Equal("error", json.GetProperty("type").GetString());
        Assert.Empty(host.Gateway.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_routes_preserve_header_and_azure_authentication(bool chat)
    {
        await using var header = await TestAgentHost.StartAsync();
        using var unauthorized = await SendAsync(header, chat, Request(chat), credential: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("Bearer", Assert.Single(unauthorized.Headers.WwwAuthenticate).Scheme);
        using var missing = await SendAsync(header, chat, Request(chat, model: "missing-agent"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(header.Gateway.Calls);

        await using var azure = await TestAgentHost.StartAsync(azure: true);
        using var forbidden = await SendAsync(azure, chat, Request(chat), TestAgentHost.Token("user", "other_scope"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var success = await SendAsync(azure, chat, Request(chat), TestAgentHost.Token("user"));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal("Bearer downstream-user", Assert.Single(azure.Gateway.Calls).Headers["Authorization"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_failure_before_first_update_is_http_and_after_is_sse(bool chat)
    {
        await using var host = await TestAgentHost.StartAsync();
        const string secret = "not-for-public-output";
        host.Gateway.Respond = _ => GatewayFixture.Reply(secret, status: HttpStatusCode.Unauthorized);
        var body = Request(chat, options: """, "stream":true""");
        using var rejected = await SendAsync(host, chat, body);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("application/json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(secret, await rejected.Content.ReadAsStringAsync());

        host.Gateway.Respond = _ => GatewayFixture.Reply(GatewayFixture.FirstDelta + $"data: {{\"type\":\"error\",\"message\":\"{secret}\"}}\n\n", stream: true);
        using var interrupted = await SendAsync(host, chat, body);
        Assert.Equal(HttpStatusCode.OK, interrupted.StatusCode);
        var sse = await interrupted.Content.ReadAsStringAsync();
        var last = Frames(sse)[^1];
        Assert.Equal("api_error", last.GetProperty("error").GetProperty("type").GetString());
        Assert.DoesNotContain(secret, sse);
        if (chat) Assert.EndsWith("data: [DONE]\n\n", sse);
        else
        {
            Assert.Contains("event: error\n", sse);
            Assert.DoesNotContain("message_stop", sse);
        }
    }

    private static string Request(bool chat, string messages = UserMessage, string options = "", string model = "GreetingAgent") => $$$"""
        {"model":"{{{model}}}", "messages":[{{{messages}}}]{{{(chat ? "" : ", \"max_tokens\":128")}}}{{{options}}}}
        """;

    private static async Task<HttpResponseMessage> SendAsync(TestAgentHost host, bool chat, string body, string? credential = "fixture-key")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, chat ? "/v1/chat/completions" : "/v1/messages")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await host.Client.SendAsync(request);
    }

    private static JsonElement[] Frames(string sse) => sse.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
        .Select(line => Parse(line[6..])).ToArray();

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}

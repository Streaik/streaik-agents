using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Streaik.Agents.Tests;

public sealed class AgentProtocolEndpointTests
{
    private const string UserMessage = """{"role":"user","content":"Say hi"}""";
    private const string WeatherArgs = """{"city":"Amsterdam"}""";
    private const string CallId = "call_weather";

    [Theory]
    [InlineData(true, false, "sequential")]
    [InlineData(false, true, "sequential")]
    [InlineData(true, true, "concurrent")]
    [InlineData(false, false, "concurrent")]
    [InlineData(true, false, "handoff")]
    [InlineData(false, false, "groupchat")]
    [InlineData(true, false, "magentic")]
    public async Task Workflow_tool_results_resume_the_paused_agents(bool chat, bool stream, string workflow)
    {
        await using var host = await TestAgentHost.StartAsync();
        var parallel = workflow == "concurrent";
        var magentic = workflow == "magentic";
        var ledgers = 0;
        host.Gateway.Respond = call =>
        {
            var model = call.Body.GetProperty("model").GetString()!;
            if (magentic && model == "openai/draft")
            {
                Assert.False(call.Body.TryGetProperty("tools", out _));
                if (call.Body.GetProperty("input").EnumerateArray().Last().GetRawText().Contains("is_request_satisfied", StringComparison.Ordinal))
                    return GatewayFixture.Text(call, JsonSerializer.Serialize(new
                    {
                        is_request_satisfied = new { reason = "Fixture", answer = ++ledgers > 1 },
                        is_in_loop = new { reason = "Fixture", answer = false },
                        is_progress_being_made = new { reason = "Fixture", answer = true },
                        next_speaker = new { reason = "Fixture", answer = "Review" },
                        instruction_or_question = new { reason = "Fixture", answer = "Review the draft." }
                    }));
                return GatewayFixture.Text(call, ledgers == 0 ? "Private plan" : "Answer from openai/draft");
            }
            var hasResult = call.Body.GetProperty("input").EnumerateArray().Any(item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            return !hasResult && (parallel || model == (magentic ? "openai/review" : "openai/draft"))
                ? GatewayFixture.Complete(call, [new { type = "function_call", id = "fc_fixture", call_id = CallId, name = "weather", arguments = WeatherArgs }])
                : GatewayFixture.Text(call, $"Answer from {model}");
        };
        var tools = chat ? """, "tools":[{"type":"function","function":{"name":"weather","parameters":{"type":"object"}}}]"""
            : """, "tools":[{"name":"weather","input_schema":{"type":"object"}}]""";
        var metadata = workflow switch
        {
            "handoff" => """, "workflow_metadata":{"handoff":{"handoffs":[["Draft",["Review"]]]}}""",
            "groupchat" => """, "workflow_metadata":{"groupchat":{"maximumIterationCount":1}}""",
            _ => ""
        };
        var start = WithoutModel(chat, """, "agents":[{"name":"Draft","model":{"id":"openai/draft"}}, {"name":"Review","model":{"id":"openai/review"}}]"""
            + $", \"workflow_type\":\"{workflow}\"" + metadata + tools + (stream ? """, "stream":true""" : ""));
        using var first = await SendAsync(host, chat, start);
        var token = await AssertPaused(first);
        if (magentic)
        {
            Assert.Single(host.Gateway.Calls, call => call.Body.GetProperty("model").GetString() == "openai/review");
            Assert.Equal(1, ledgers);
        }
        else Assert.Equal(parallel ? 2 : 1, host.Gateway.Calls.Count);
        var wrongResult = Resume(chat, token, "wrong", stream);
        using var wrong = await SendAsync(host, chat, wrongResult);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var wrongOwner = await SendAsync(host, chat, Resume(chat, token, CallId, stream), "different-key");
        Assert.Equal(HttpStatusCode.BadRequest, wrongOwner.StatusCode);
        using var resumed = await SendAsync(host, chat, Resume(chat, token, CallId, stream));
        using var replay = await SendAsync(host, chat, Resume(chat, token, CallId, stream));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        if (parallel)
        {
            var secondToken = await AssertPaused(resumed);
            Assert.NotEqual(token, secondToken);
            using var completed = await SendAsync(host, chat, Resume(chat, secondToken, CallId, stream));
            var finalText = await completed.Content.ReadAsStringAsync();
            Assert.True(completed.IsSuccessStatusCode, finalText);
            Assert.Contains("Answer from openai/draft", finalText);
            Assert.DoesNotContain("Private plan", finalText);
            Assert.Contains("Answer from openai/review", finalText);
        }
        else
        {
            var finalText = await resumed.Content.ReadAsStringAsync();
            Assert.True(resumed.IsSuccessStatusCode, finalText);
            Assert.Contains("Answer from openai/draft", finalText);
            Assert.DoesNotContain("workflow_continuation", finalText);
        }
        var returning = host.Gateway.Calls.Where(call => call.Body.GetRawText().Contains("function_call_output", StringComparison.Ordinal)).ToArray();
        Assert.Contains(returning, call => call.Body.GetProperty("model").GetString() == (magentic ? "openai/review" : "openai/draft"));
        Assert.Equal(parallel || magentic ? 2 : workflow == "sequential" ? 1 : 0,
            host.Gateway.Calls.Count(call => call.Body.GetProperty("model").GetString() == "openai/review"));
        if (magentic) Assert.Equal(2, ledgers);
        else Assert.Equal(2, host.Gateway.Calls.Count(call => call.Body.GetProperty("model").GetString() == "openai/draft"));
        Assert.All(host.Gateway.Calls, call => Assert.False(call.Body.TryGetProperty("workflow_continuation", out _)));

        async Task<string> AssertPaused(HttpResponseMessage reply)
        {
            var content = await reply.Content.ReadAsStringAsync();
            Assert.True(reply.IsSuccessStatusCode, content);
            Assert.Contains(CallId, content);
            Assert.Contains(chat ? "\"finish_reason\":\"tool_calls\"" : "\"stop_reason\":\"tool_use\"", content);
            var frames = stream ? Frames(content) : [Parse(content)];
            return Assert.Single(frames, frame => frame.TryGetProperty("workflow_continuation", out _))
                .GetProperty("workflow_continuation").GetString()!;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Decisions_preserves_the_winner_across_repeated_tool_rounds(bool chat)
    {
        await using var host = await TestAgentHost.StartAsync();
        var turns = 0;
        host.Gateway.Respond = call => call.Endpoint.AbsolutePath.EndsWith("/api/decisions", StringComparison.Ordinal)
            ? GatewayFixture.Reply("""{"answers":{"agent":{"type":"choice","choice":"agent-1"}}}""")
            : ++turns <= 2 ? GatewayFixture.Complete(call, [new { type = "function_call", id = "fc_fixture", call_id = CallId, name = "weather", arguments = WeatherArgs }])
            : GatewayFixture.Text(call, "Decision winner completed");
        var tools = chat ? """, "tools":[{"type":"function","function":{"name":"weather"}}]"""
            : """, "tools":[{"name":"weather","input_schema":{"type":"object"}}]""";
        using var initial = await SendAsync(host, chat, WithoutModel(chat,
            """, "models":["GreetingAgent","ReviewAgent"], "workflow_type":"decisions", "workflow_metadata":{"decisions":{"model":"openai/router"}}""" + tools));
        var first = await Token(initial);
        using var second = await SendAsync(host, chat, Resume(chat, first, CallId, false));
        using var completed = await SendAsync(host, chat, Resume(chat, await Token(second), CallId, false));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Contains("Decision winner completed", await completed.Content.ReadAsStringAsync());
        Assert.Single(host.Gateway.Calls, call => call.Endpoint.AbsolutePath.EndsWith("/api/decisions", StringComparison.Ordinal));
        Assert.All(host.Gateway.Calls.Where(call => call.Endpoint.AbsolutePath.EndsWith("/v1/responses", StringComparison.Ordinal)),
            call => Assert.Equal("openai/review", call.Body.GetProperty("model").GetString()));
        Assert.Equal(3, turns);

        static async Task<string> Token(HttpResponseMessage reply)
        {
            var json = await reply.Content.ReadAsStringAsync();
            Assert.True(reply.IsSuccessStatusCode, json);
            return Parse(json).GetProperty("workflow_continuation").GetString()!;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Catalog_and_inline_extensions_keep_native_response_shapes_and_validate_input(bool chat)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var inline = await SendAsync(host, chat, WithoutModel(chat,
            """, "agent":{"name":"Inline","instructions":"Inline instructions","model":{"id":"openai/inline"}}"""));
        var json = await inline.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("Inline", json.GetProperty("model").GetString());
        Assert.Contains("Inline instructions", Assert.Single(host.Gateway.Calls).Body.GetRawText());

        using var catalog = await SendAsync(host, chat, WithoutModel(chat, """, "models":["greetingagent","ReviewAgent"]"""));
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var workflow = await catalog.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sequential", workflow.GetProperty("model").GetString());
        Assert.Contains("Hello there", workflow.GetRawText());
        Assert.Equal(3, host.Gateway.Calls.Count);
        foreach (var options in new[] { "", ", \"models\":\"GreetingAgent\"", """, "agents":[null]""",
                     ", \"models\":[\"GreetingAgent\"],\"workflowType\":\"concurrent\"", """, "models":["GreetingAgent"],"unknown":true""" })
        {
            using var invalid = await SendAsync(host, chat, WithoutModel(chat, options));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(3, host.Gateway.Calls.Count);
    }

    private static string Resume(bool chat, string token, string id, bool stream) =>
        $"{{\"workflow_continuation\":\"{token}\",\"stream\":{(stream ? "true" : "false")}," + (chat
            ? $$$"""
                "messages":[{"role":"tool","tool_call_id":"{{{id}}}","content":"sunny"}]}
                """
            : $$$"""
                "max_tokens":128,"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{id}}}","content":"sunny"}]}]}
                """);

    private static string WithoutModel(bool chat, string options) => $$$"""
        {"messages":[{{{UserMessage}}}]{{{(chat ? "" : ", \"max_tokens\":128")}}}{{{options}}}}
        """;

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

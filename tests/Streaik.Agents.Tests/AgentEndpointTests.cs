using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streaik.Tools;

namespace Streaik.Agents.Tests;

public sealed class AgentEndpointTests
{
    private const string Chat = "/api/chat";
    private const string Responses = "/v1/responses";
    private static readonly object ChatBody = new
    {
        model = "GreetingAgent",
        messages = new[] { new { id = "user1", role = "user", parts = new[] { new { type = "text", text = "Say hi" } } } }
    };
    private static readonly object ResponseBody = new { model = "GreetingAgent", input = "Say hi" };

    [Fact]
    public async Task ApiChat_converts_a_real_provider_approval_in_process_and_uses_the_client_result()
    {
        var endpoint = new Uri("https://inventory.example.test/mcp");
        var calls = 0;
        await using var host = await TestAgentHost.StartAsync(azure: true, mcp: new HostedToolAccess(
            (label, url, _, _) => ValueTask.FromResult(label == "inventory" && url == endpoint
                ? new McpEndpointAccess { ToolNames = new HashSet<string> { "lookup" } } : null),
            CreateMcpHandler: _ => new InventoryMcp(() => calls++)));
        var request = JsonNode.Parse("""
            {"id":"mcp-review-chat","agent":{"name":"Inventory","model":{"id":"openai/test"},"tools":[
              {"type":"mcp","execution":"provider","server_label":"inventory","server_url":"https://inventory.example.test/mcp",
               "namespace":true,"disabled":true,"required":true,"require_approval":"always"}]},
             "messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"look up"}]}]}
            """)!;
        host.Gateway.Respond = call => GatewayFixture.Complete(call,
            [new { type = "mcp_approval_request", id = "mcpr_native", server_label = "inventory", name = "lookup", arguments = "{}" }]);
        using var first = await host.Client.SendAsync(TestAgentHost.Post(Chat, request, TestAgentHost.Token("alice")));
        var events = Events(await first.Content.ReadAsStringAsync(), out _);
        var approval = Assert.Single(events, part => EventType(part) == "tool-approval-request");
        Assert.Equal(1, calls);
        Assert.Equal("inventory result", approval.GetProperty("approvalDescriptor").GetProperty("output").GetProperty("content")[0].GetProperty("text").GetString());
        var initialBody = host.Gateway.Calls.Single().Body;
        Assert.DoesNotContain("inventory result", initialBody.GetRawText());
        var mcp = Assert.Single(initialBody.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("type").ValueEquals("mcp"));
        Assert.Equal("always", mcp.GetProperty("require_approval").GetString());
        Assert.False(mcp.TryGetProperty("namespace", out _));
        Assert.False(mcp.TryGetProperty("disabled", out _));
        Assert.False(mcp.TryGetProperty("required", out _));
        var id = approval.GetProperty("approvalId").GetString();
        request["messages"]!.AsArray().Add(JsonNode.Parse($$$"""
            {"id":"a","role":"assistant","parts":[{"type":"dynamic-tool","toolName":"streaik_mcp_result_review","toolCallId":"{{{id}}}",
              "providerExecuted":true,"state":"approval-responded","input":{},"output":"client-selected result","approval":{"id":"{{{id}}}","approved":true}}]}
            """));
        host.Gateway.Respond = call => GatewayFixture.Text(call, "done");
        using var wrongUser = await host.Client.SendAsync(TestAgentHost.Post(Chat, request, TestAgentHost.Token("bob")));
        Assert.Equal(HttpStatusCode.BadRequest, wrongUser.StatusCode);
        using var resumed = await host.Client.SendAsync(TestAgentHost.Post(Chat, request, TestAgentHost.Token("alice")));
        Assert.Contains("done", await resumed.Content.ReadAsStringAsync());
        var input = host.Gateway.Calls.Last().Body.GetProperty("input").GetRawText();
        Assert.Contains("client-selected result", input);
        Assert.DoesNotContain("inventory result", input);
        Assert.Equal(1, calls);
    }

    private sealed class InventoryMcp(Action invoke) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null) return new(HttpStatusCode.NoContent);
            using var doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = doc.RootElement.GetProperty("method").GetString();
            if (method == "server/discover") return GatewayFixture.Reply($$$"""{"jsonrpc":"2.0","id":{{{id.GetRawText()}}},"error":{"code":-32601,"message":"Unknown method"}}""");
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"inventory","version":"1"}}""",
                "tools/list" => """{"tools":[{"name":"lookup","inputSchema":{"type":"object"}}]}""",
                "tools/call" => """{"content":[{"type":"text","text":"inventory result"}]}""",
                _ => throw new InvalidOperationException("Unexpected MCP method.")
            };
            if (method == "tools/call") invoke();
            return GatewayFixture.Reply($$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{result}}}""");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Browser_calls_get_a_signed_approval_and_only_reviewed_output_is_forwarded(bool approved)
    {
        await using var host = await TestAgentHost.StartAsync();
        var request = JsonNode.Parse("""
            {"model":"GreetingAgent","id":"test-browser","tools":[{"name":"browser_lookup","inputSchema":{"type":"object"}}],
             "messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"look up"}]}]}
            """)!;
        host.Gateway.Respond = call => call.Body.GetProperty("input").EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var kind) && kind.ValueEquals("function_call_output"))
            ? GatewayFixture.Text(call, "done") : GatewayFixture.Complete(call,
            [new { type = "function_call", call_id = "call_browser", name = "browser_lookup", arguments = "{\"query\":\"q\"}" }]);
        using var first = await host.Client.SendAsync(TestAgentHost.Post(Chat, request, "alice-key"));
        var events = Events(await first.Content.ReadAsStringAsync(), out _);
        var call = Assert.Single(events, part => EventType(part) == "tool-input-available");
        var approval = Assert.Single(events, part => EventType(part) == "tool-approval-request");
        Assert.False(call.GetProperty("providerExecuted").GetBoolean());
        Assert.Equal("call_browser", approval.GetProperty("toolCallId").GetString());
        var invocation = new JsonObject { ["type"] = "dynamic-tool", ["toolCallId"] = "call_browser", ["toolName"] = "browser_lookup",
            ["state"] = approved ? "output-available" : "approval-responded", ["providerExecuted"] = false,
            ["input"] = JsonNode.Parse(call.GetProperty("input").GetRawText()), ["output"] = "private",
            ["approval"] = new JsonObject { ["id"] = approval.GetProperty("approvalId").GetString(),
                ["signature"] = approval.GetProperty("signature").GetString(), ["approved"] = approved } };
        request["messages"]!.AsArray().Add(new JsonObject { ["id"] = "a", ["role"] = "assistant", ["parts"] = new JsonArray(invocation) });
        using var resumed = await host.Client.SendAsync(TestAgentHost.Post(Chat, request, "alice-key"));
        Assert.Contains("done", await resumed.Content.ReadAsStringAsync());
        var forwarded = host.Gateway.Calls.Last().Body.GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type) && type.ValueEquals("function_call_output"));
        Assert.Equal(approved ? "private" : "The user declined to share this tool result.", forwarded.GetProperty("output").GetString());
        invocation["input"]!["query"] = "tampered";
        using var invalid = await host.Client.SendAsync(TestAgentHost.Post(Chat, request, "alice-key"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(2, host.Gateway.Calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Responses_preserves_native_approval_items(bool stream)
    {
        await using var host = await TestAgentHost.StartAsync(azure: true);
        host.Gateway.Respond = call => GatewayFixture.Complete(call,
            [new { type = "mcp_approval_request", id = "mcpr_real", server_label = "inventory", name = "lookup", arguments = "{}" }]);
        using var response = await host.Client.SendAsync(TestAgentHost.Post(Responses, new { model = "GreetingAgent", input = "Find it", stream }, TestAgentHost.Token("alice")));
        var wire = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mcpr_real", wire);
        if (stream) Assert.Contains("response.output_item.done", wire);
        var decision = new { model = "GreetingAgent", input = new object[]
        {
            new { role = "user", content = "Find it" },
            new { type = "mcp_approval_response", approval_request_id = "mcpr_real", approve = false }
        }};
        host.Gateway.Respond = call => GatewayFixture.Text(call, "not executed");
        using var wrongUser = await host.Client.SendAsync(TestAgentHost.Post(Responses, decision, TestAgentHost.Token("bob")));
        Assert.Equal(HttpStatusCode.BadRequest, wrongUser.StatusCode);
        using var resumed = await host.Client.SendAsync(TestAgentHost.Post(Responses, decision, TestAgentHost.Token("alice")));
        Assert.Contains("not executed", await resumed.Content.ReadAsStringAsync());
        Assert.Contains("mcp_approval_response", host.Gateway.Calls.Last().Body.GetRawText());
        using var repeated = await host.Client.SendAsync(TestAgentHost.Post(Responses, decision, TestAgentHost.Token("alice")));
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(2, host.Gateway.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_agent_routes_use_the_same_gateway_with_the_right_credentials(bool azure)
    {
        await using var host = await TestAgentHost.StartAsync(azure);
        var credential = azure ? TestAgentHost.Token("alice") : "alice-key";
        using var chatRequest = TestAgentHost.Post(Chat, ChatBody, credential);
        using var responseRequest = TestAgentHost.Post(Responses, ResponseBody, credential);
        foreach (var request in new[] { chatRequest, responseRequest })
        {
            request.Headers.Add("x-anthropic-key", "unrelated-key");
            request.Headers.Add("Cookie", "private=1");
            if (azure) request.Headers.Add("x-openai-key", "caller-key-not-allowed");
        }

        using var chat = await host.Client.SendAsync(chatRequest);
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Equal("text/event-stream", chat.Content.Headers.ContentType?.MediaType);
        Assert.Equal("v1", Assert.Single(chat.Headers.GetValues("x-vercel-ai-ui-message-stream")));
        var events = Events(await chat.Content.ReadAsStringAsync(), out var done);
        Assert.True(done);
        Assert.Equal(new[] { "start", "text-start", "text-delta", "text-delta", "text-end", "finish" }, events.Select(EventType));
        var textId = events[1].GetProperty("id").GetString();
        Assert.All(events.Skip(2).Take(3), part => Assert.Equal(textId, part.GetProperty("id").GetString()));
        Assert.Equal("Hello there", string.Concat(events.Where(e => EventType(e) == "text-delta").Select(e => e.GetProperty("delta").GetString())));

        using var responses = await host.Client.SendAsync(responseRequest);
        Assert.Equal(HttpStatusCode.OK, responses.StatusCode);
        using var json = JsonDocument.Parse(await responses.Content.ReadAsStringAsync());
        var result = json.RootElement;
        Assert.Equal("response", result.GetProperty("object").GetString());
        Assert.Equal("GreetingAgent", result.GetProperty("model").GetString());
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal("Hello there", OutputText(result));
        Assert.Equal(7, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        Assert.Equal(2, host.Gateway.Calls.Count);
        Assert.All(host.Gateway.Calls, call =>
        {
            Assert.Equal("https://gateway.example.test/base/v1/responses", call.Endpoint.AbsoluteUri);
            Assert.False(call.Headers.ContainsKey("Cookie"));
            Assert.False(call.Headers.ContainsKey("x-anthropic-key"));
            Assert.Equal("openai/test", call.Body.GetProperty("model").GetString());
            Assert.False(call.Body.GetProperty("store").GetBoolean());
            Assert.Equal("Reply with a greeting.", call.Body.GetProperty("instructions").GetString());
            Assert.Equal("Say hi", call.Body.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
            if (azure)
            {
                Assert.Equal("Bearer downstream-alice", call.Headers["Authorization"]);
                Assert.False(call.Headers.ContainsKey("x-openai-key"));
            }
            else
            {
                Assert.Equal("alice-key", call.Headers["x-openai-key"]);
                Assert.False(call.Headers.ContainsKey("Authorization"));
            }
        });
    }

    [Theory]
    [InlineData("v1/responses")]
    [InlineData("responses")]
    public async Task Inline_transport_streams_ordered_responses_events_and_maps_history(string transport)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = TestAgentHost.Post(Responses, new
        {
            agent = new { name = "Inline", model = new { id = "openai/test" }, instructions = "Agent instructions", transport },
            input = new[] { new { role = "user", content = "Q1" }, new { role = "assistant", content = "A1" }, new { role = "user", content = "Q2" } },
            instructions = "Turn instructions",
            stream = true,
            max_output_tokens = 50
        }, "ignored-byok");
        request.Headers.Add("x-openai-key", "explicit-key");
        using var result = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("text/event-stream", result.Content.Headers.ContentType?.MediaType);
        var events = Events(await result.Content.ReadAsStringAsync(), out var done);
        Assert.True(done);
        Assert.Equal(new[] { "response.created", "response.output_item.added", "response.content_part.added", "response.output_text.delta",
            "response.output_text.delta", "response.output_text.done", "response.content_part.done", "response.output_item.done", "response.completed" },
            events.Select(EventType));
        Assert.Equal(Enumerable.Range(0, events.Length), events.Select(e => e.GetProperty("sequence_number").GetInt32()));
        var completed = events[^1].GetProperty("response");
        Assert.Equal(events[0].GetProperty("response").GetProperty("id").GetString(), completed.GetProperty("id").GetString());
        Assert.Equal("Inline", completed.GetProperty("model").GetString());
        Assert.Equal("Hello there", OutputText(completed));
        Assert.Equal(7, completed.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        var call = Assert.Single(host.Gateway.Calls);
        Assert.Equal("/base/v1/responses", call.Endpoint.AbsolutePath);
        Assert.Equal("explicit-key", call.Headers["x-openai-key"]);
        Assert.Equal("Agent instructions", call.Body.GetProperty("instructions").GetString());
        Assert.Equal(50, call.Body.GetProperty("max_output_tokens").GetInt32());
        var history = call.Body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, history.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("output_text", history[2].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("v1/messages")]
    [InlineData("v1/chat/completions")]
    [InlineData("")]
    public async Task Unsupported_transports_are_rejected_before_streaming_or_inference(string transport)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = TestAgentHost.Post(Responses, new
        {
            agent = new { name = "Inline", model = new { id = "openai/test" }, transport }, input = "Hi", stream = true
        }, "fake-key");
        using var result = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("unsupported_transport", await ErrorCodeAsync(result));
        Assert.Empty(host.Gateway.Calls);
    }

    [Theory]
    [InlineData("/v1/responses", "{\"model\":\"GreetingAgent\",\"input\":\"Hi\",\"workflow_type\":\"unknown\"}", HttpStatusCode.BadRequest)]
    [InlineData("/v1/responses", "{\"model\":\"GreetingAgent\",\"input\":\"Hi\",\"previous_response_id\":\"resp_1\"}", HttpStatusCode.BadRequest)]
    [InlineData("/v1/responses", "{\"model\":\"MissingAgent\",\"input\":\"Hi\"}", HttpStatusCode.NotFound)]
    [InlineData("/v1/responses", "{\"model\":\"GreetingAgent\",\"input\":\"Hi\",\"agent\":{}}", HttpStatusCode.BadRequest)]
    [InlineData("/v1/responses", """{"model":"GreetingAgent","input":"Hi","workflowMetadata":null}""", HttpStatusCode.BadRequest)]
    [InlineData("/api/chat", """{"model":"GreetingAgent","messages":[{"role":"user","parts":[{"type":"text","text":"Hi"}]}],"workflowMetadata":{},"workflow_metadata":{}}""", HttpStatusCode.BadRequest)]
    [InlineData("/v1/responses", """{"agent":{"name":"Inline","model":{"id":"router/test","type":"decision"}},"input":"Hi"}""", HttpStatusCode.BadRequest)]
    [InlineData("/api/chat", "{\"agents\":[{},{}],\"messages\":[]}", HttpStatusCode.BadRequest)]
    public async Task Unsupported_routing_shapes_are_not_silently_ignored(string path, string json, HttpStatusCode status)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.Empty(host.Gateway.Calls);
    }

    [Theory]
    [InlineData(Chat)]
    [InlineData(Responses)]
    public async Task Azure_routes_require_a_valid_delegated_token(string path)
    {
        await using var host = await TestAgentHost.StartAsync(azure: true);
        var body = path == Chat ? ChatBody : ResponseBody;
        foreach (var (token, status) in new[] { ((string?)null, HttpStatusCode.Unauthorized), ("invalid", HttpStatusCode.Unauthorized),
            (TestAgentHost.Token("alice", "other_scope"), HttpStatusCode.Forbidden) })
        {
            using var request = TestAgentHost.Post(path, body, token);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
        }
        Assert.Empty(host.Gateway.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_calls_do_not_reuse_another_callers_credentials(bool azure)
    {
        await using var host = await TestAgentHost.StartAsync(azure);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var request = TestAgentHost.Post(Responses, new { model = "GreetingAgent", input = $"user{i}" },
                azure ? TestAgentHost.Token($"user{i}") : $"key{i}");
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));
        Assert.Equal(8, host.Gateway.Calls.Count);
        foreach (var call in host.Gateway.Calls)
        {
            var user = call.Body.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Equal(azure ? $"Bearer downstream-{user}" : $"key{user[4..]}", call.Headers[azure ? "Authorization" : "x-openai-key"]);
        }
    }

    [Fact]
    public async Task Header_host_requires_a_matching_provider_key()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = TestAgentHost.Post(Responses, ResponseBody);
        request.Headers.Add("x-anthropic-key", "unrelated");
        using var result = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("gateway_authentication", await ErrorCodeAsync(result));
        Assert.Empty(host.Gateway.Calls);
    }

    [Theory]
    [InlineData(Chat)]
    [InlineData(Responses)]
    public async Task A_truncated_upstream_stream_reports_failure_not_success(string path)
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = _ => GatewayFixture.Reply(GatewayFixture.FirstDelta + "data: [DONE]\n\n", stream: true);
        using var request = TestAgentHost.Post(path, path == Chat ? ChatBody : new { model = "GreetingAgent", input = "Say hi", stream = true }, "fake-key");
        using var response = await host.Client.SendAsync(request);
        var events = Events(await response.Content.ReadAsStringAsync(), out var done);
        Assert.True(done);
        Assert.Equal(path == Chat ? "error" : "response.failed", EventType(events[^1]));
        Assert.DoesNotContain(events, e => EventType(e) is "finish" or "response.completed");
    }

    [Fact]
    public async Task Upstream_error_details_are_not_exposed()
    {
        await using var host = await TestAgentHost.StartAsync();
        host.Gateway.Respond = _ => GatewayFixture.Reply("""{"error":{"message":"private upstream detail"}}""", status: HttpStatusCode.TooManyRequests);
        using var request = TestAgentHost.Post(Chat, ChatBody, "fake-key");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.DoesNotContain("private upstream detail", await response.Content.ReadAsStringAsync());
        Assert.Equal("rate_limit_exceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Disconnecting_from_the_route_cancels_the_gateway_stream()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var upstream = new CancellableBody();
        host.Gateway.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(upstream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } }
        };
        using var request = TestAgentHost.Post(Chat, ChatBody, "fake-key");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await upstream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        response.Dispose();
        await upstream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string? OutputText(JsonElement response) => response.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
    private static string? EventType(JsonElement value) => value.GetProperty("type").GetString();
    private static JsonElement[] Events(string stream, out bool done)
    {
        var data = stream.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        done = data.LastOrDefault() == "[DONE]";
        return data.Where(line => line != "[DONE]").Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private sealed class CancellableBody() : MemoryStream(Encoding.UTF8.GetBytes(GatewayFixture.FirstDelta))
    {
        public override bool CanSeek => false;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            if (read > 0) return read;
            Waiting.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return 0;
        }
    }
}

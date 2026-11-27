using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Streaik.Tools;

namespace Streaik.Agents.Tests;

public sealed class AgentSkillTests
{
    private const string Responses = "/v1/responses";
    private const string Chat = "/api/chat";
    private const string RemoteId = "anthropic/skill_gateway";
    private const string ExistingInstructions = "Answer with the available material.";
    private const string InlineInstructions = "Use the planning checklist.";
    private const string RemoteInstructions = "Use the notes from the reference.";
    private const string ResourceText = "Checklist reference content.";

    [Fact]
    public async Task Gateway_execution_uses_the_same_tools_array_without_loading_the_reference_on_the_agent()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = TestAgentHost.Post(Responses, new
        {
            input = "Make a plan.", agent = new { name = "SkillAgent", model = new { id = "openai/test" }, tools = new[]
            {
                new { type = "skills", execution = "server", skills = new[] { new { type = "skill_reference", skill_id = RemoteId } } }
            } }
        });
        request.Headers.Add("x-openai-key", "answer-key");
        using var response = await host.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var sent = Assert.Single(host.Gateway.Calls).Body;
        var tool = Assert.Single(sent.GetProperty("tools").EnumerateArray());
        Assert.Equal("skills", tool.GetProperty("name").GetString());
        Assert.Equal("skills", tool.GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("execution", out _));
        Assert.False(sent.TryGetProperty("hosted_tools", out _));
    }

    [Theory]
    [InlineData(Chat)]
    [InlineData(Responses)]
    public async Task Only_API_Chat_opts_into_and_relays_gateway_hosted_activity(string endpoint)
    {
        await using var host = await TestAgentHost.StartAsync();
        const string callId = "call_delegated";
        host.Gateway.Respond = call =>
        {
            var completed = GatewayFixture.Completed.ReplaceLineEndings("");
            string Frame(string id, object? output = null) => "data: " + JsonSerializer.Serialize(new
            {
                type = HostedToolActivity.ResponsesEvent,
                activity = new { name = "streaik_activate_skill", callId = id, input = new { skill_id = RemoteId }, output }
            }) + "\n\n";
            var prefix = call.Headers.ContainsKey(HostedToolActivity.ResponsesHeader)
                ? Frame(callId) + Frame("wrong_id", new { value = "Unmatched output" }) + Frame(callId, new { value = "Hosted skill result" }) : "";
            return GatewayFixture.Reply(prefix + $$"""
                data: {"type":"response.completed","response":{{completed}}}

                data: [DONE]

                """, stream: true);
        };
        var agent = new { name = "Delegated", model = new { id = "openai/test" }, tools = new[]
        {
            new { type = "skills", execution = "server", skills = new[] { Inline(Bundle("planning", "Plan.", InlineInstructions)) } }
        } };
        using var request = TestAgentHost.Post(endpoint, endpoint == Chat
            ? new { agent, stream = true, messages = new[] { new { id = "u", role = "user", parts = new[] { new { type = "text", text = "hello" } } } } }
            : (object)new { agent, stream = true, input = "hello" });
        request.Headers.Add("x-openai-key", "answer-key");
        using var response = await host.Client.SendAsync(request);
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);
        var upstream = Assert.Single(host.Gateway.Calls);
        Assert.Equal(endpoint == Chat, upstream.Headers.ContainsKey(HostedToolActivity.ResponsesHeader));
        Assert.Contains("Hello there", wire);
        Assert.DoesNotContain("Unmatched output", wire);
        Assert.DoesNotContain("tool-approval-request", wire);
        Assert.DoesNotContain(HostedToolActivity.ResponsesEvent, wire);
        var activity = ToolFrames(wire);
        if (endpoint != Chat) { Assert.Empty(activity); return; }
        Assert.Collection(activity,
            call => AssertActivity(call, "tool-input-available", callId),
            output => AssertActivity(output, "tool-output-available", callId));
        Assert.Equal("streaik_activate_skill", activity[0].GetProperty("toolName").GetString());
        Assert.Equal(RemoteId, activity[0].GetProperty("input").GetProperty("skill_id").GetString());
        Assert.Equal("Hosted skill result", activity[1].GetProperty("output").GetProperty("value").GetString());
        Assert.True(wire.IndexOf("Hosted skill result", StringComparison.Ordinal) < wire.IndexOf("Hello there", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Chat, true, null)]
    [InlineData(Responses, false, "2")]
    [InlineData(Responses, true, "latest")]
    public async Task Agent_routes_discover_and_run_the_same_two_tools(string endpoint, bool stream, string? version)
    {
        await using var host = await TestAgentHost.StartAsync();
        var remote = Bundle("gateway-bundle", "Summarize\n  meeting notes.", RemoteInstructions);
        var inline = Inline(Bundle("planning", "Create a checklist.", InlineInstructions));
        var step = 0;
        host.Gateway.Respond = call => call.Body.ValueKind == JsonValueKind.Null ? Zip(remote) : ++step switch
        {
            1 => ToolCall(call, step, "activate_skill", new { skill_id = "planning" }),
            2 => ToolCall(call, step, "activate_skill", new { skill_id = RemoteId }),
            3 => ToolCall(call, step, "read_skill_resource", new { skill_id = RemoteId, path = "references/notes.md" }),
            4 => ToolCall(call, step, "read_skill_resource", new { skill_id = "planning", path = "assets/fixture.bin" }),
            _ => GatewayFixture.Text(call, "Skill run complete")
        };

        using var request = Request(endpoint, stream, [inline, new { type = "skill_reference", skill_id = RemoteId, version }]);
        request.Headers.Add("x-openai-key", "answer-key");
        request.Headers.Add("x-anthropic-key", "skill-key");
        request.Headers.Add("x-mistral-key", "unrelated-key");
        using var result = await host.Client.SendAsync(request);
        var responseText = await result.Content.ReadAsStringAsync();
        Assert.True(result.IsSuccessStatusCode, responseText);
        Assert.Contains("Skill run complete", responseText);
        Assert.DoesNotContain("tool-approval-request", responseText);
        var activity = ToolFrames(responseText);
        if (endpoint == Chat)
        {
            Assert.Equal(8, activity.Length);
            for (var i = 1; i <= 4; i++)
            {
                AssertActivity(activity[(i - 1) * 2], "tool-input-available", $"call_{i}");
                AssertActivity(activity[(i - 1) * 2 + 1], "tool-output-available", $"call_{i}");
            }
            Assert.Equal("read_skill_resource", activity[4].GetProperty("toolName").GetString());
            Assert.Equal(ResourceText, activity[5].GetProperty("output").GetProperty("structuredContent").GetProperty("skillResource").GetProperty("text").GetString());
        }
        else Assert.Empty(activity);

        var calls = host.Gateway.Calls.ToArray();
        Assert.Equal(6, calls.Length);
        var download = calls[0];
        var versionPart = version is null ? string.Empty : $"/versions/{Uri.EscapeDataString(version)}";
        Assert.Equal($"https://gateway.example.test/base/v1/skills/{RemoteId}{versionPart}/content", download.Endpoint.AbsoluteUri);
        Assert.Equal("application/zip", download.Headers["Accept"]);
        Assert.Equal("skill-key", download.Headers["x-anthropic-key"]);
        Assert.False(download.Headers.ContainsKey("x-openai-key"));
        Assert.False(download.Headers.ContainsKey("x-mistral-key"));

        var initial = calls[1].Body;
        Assert.All(calls.Skip(1), call =>
        {
            Assert.Equal("https://gateway.example.test/base/v1/responses", call.Endpoint.AbsoluteUri);
            Assert.Equal("answer-key", call.Headers["x-openai-key"]);
            Assert.False(call.Headers.ContainsKey("x-anthropic-key"));
            Assert.False(call.Headers.ContainsKey("x-mistral-key"));
            Assert.Equal(initial.GetProperty("instructions").GetString(), call.Body.GetProperty("instructions").GetString());
        });

        var instructions = initial.GetProperty("instructions").GetString()!.Split("\n\n", StringSplitOptions.None);
        Assert.Equal(2, instructions.Length);
        Assert.Equal(ExistingInstructions, instructions[0]);
        Assert.Contains("activate_skill", instructions[1]);
        using var catalog = JsonDocument.Parse(instructions[1][(instructions[1].IndexOf('\n') + 1)..]);
        Assert.Equal("skills", Assert.Single(catalog.RootElement.EnumerateObject()).Name);
        var skills = catalog.RootElement.GetProperty("skills").EnumerateArray().ToArray();
        Assert.Equal(2, skills.Length);
        AssertSkill(skills[0], "Planning Guide", "planning", "Create a checklist.");
        AssertSkill(skills[1], "gateway-bundle", RemoteId, "Summarize meeting notes.");
        Assert.DoesNotContain(InlineInstructions, initial.GetRawText());
        Assert.DoesNotContain(RemoteInstructions, initial.GetRawText());
        Assert.DoesNotContain(ResourceText, initial.GetRawText());
        Assert.DoesNotContain("references/notes.md", initial.GetRawText());

        var tools = initial.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(2, tools.Length);
        AssertTool(tools[0], "activate_skill", ["skill_id"]);
        AssertTool(tools[1], "read_skill_resource", ["skill_id", "path"]);

        var inlineResult = ReadOutput(calls[2], 1).GetProperty("structuredContent").GetProperty("skill");
        Assert.Equal(InlineInstructions, inlineResult.GetProperty("instructions").GetString());
        Assert.Equal("planning", inlineResult.GetProperty("skill_id").GetString());
        var remoteResult = ReadOutput(calls[3], 2).GetProperty("structuredContent").GetProperty("skill");
        Assert.Equal(RemoteInstructions, remoteResult.GetProperty("instructions").GetString());
        Assert.Equal(RemoteId, remoteResult.GetProperty("skill_id").GetString());
        Assert.Contains("references/notes.md", remoteResult.GetProperty("resourcePaths").EnumerateArray().Select(value => value.GetString()));
        var text = ReadOutput(calls[4], 3).GetProperty("structuredContent").GetProperty("skillResource");
        Assert.Equal(ResourceText, text.GetProperty("text").GetString());
        var binary = ReadOutput(calls[5], 4).GetProperty("structuredContent").GetProperty("skillResource");
        Assert.Equal("base64", binary.GetProperty("encoding").GetString());
        Assert.Equal("AP8=", binary.GetProperty("data").GetString());
    }

    [Fact]
    public async Task No_skills_does_not_add_catalog_or_tools()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = Request(Responses, false, []);
        request.Headers.Add("x-openai-key", "answer-key");
        using var result = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var call = Assert.Single(host.Gateway.Calls);
        Assert.Equal(ExistingInstructions, call.Body.GetProperty("instructions").GetString());
        Assert.False(call.Body.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task A_mixed_provider_skill_run_does_not_reuse_an_unscoped_key()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = Request(Responses, false, [new { type = "skill_reference", skill_id = RemoteId }]);
        request.Headers.Authorization = new("Bearer", "unscoped-key");
        request.Headers.Add("x-openai-key", "answer-key");
        using var result = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Empty(host.Gateway.Calls);
    }

    [Fact]
    public async Task Invalid_reference_segments_and_unsafe_ZIP_paths_are_rejected_before_inference()
    {
        await using var host = await TestAgentHost.StartAsync();
        object[] invalid = [
            new { type = "skill_reference", skill_id = "openai/.." },
            new { type = "skill_reference", skill_id = "openai/test", version = "../elsewhere" },
            Inline(Bundle("planning", "Plan.", InlineInstructions, resource: "../notes.md"))
        ];
        foreach (var skill in invalid)
        {
            using var request = Request(Responses, false, [skill]);
            request.Headers.Add("x-openai-key", "answer-key");
            using var result = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Contains("invalid_agent_skill", await result.Content.ReadAsStringAsync());
        }
        Assert.Empty(host.Gateway.Calls);
    }

    private static HttpRequestMessage Request(string endpoint, bool stream, object[] skills)
    {
        var agent = new { name = "SkillAgent", model = new { id = "openai/test" }, instructions = ExistingInstructions,
            tools = skills.Length == 0 ? null : new[] { new { type = "skills", skills } } };
        return TestAgentHost.Post(endpoint, endpoint == Chat ? new
        {
            agent, stream,
            messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "Make a plan." } } } }
        } : (object)new { agent, stream, input = "Make a plan." });
    }

    private static object Inline(byte[] zip) => new
    {
        type = "inline", name = "Planning Guide", description = "Configured description is not the bundle description.",
        source = new { type = "base64", media_type = "application/zip", data = Convert.ToBase64String(zip) }
    };

    private static byte[] Bundle(string name, string description, string instructions, string resource = "references/notes.md")
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry($"{name}/SKILL.md").Open()))
                writer.Write($"---\nname: {name}\ndescription: >-\n  {description}\nmetadata:\n  author: fixture\n---\n\n{instructions}\n");
            using (var writer = new StreamWriter(zip.CreateEntry($"{name}/{resource}").Open())) writer.Write(ResourceText);
            using (var binary = zip.CreateEntry($"{name}/assets/fixture.bin").Open()) binary.Write([0, 255]);
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage Zip(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage ToolCall(GatewayCall call, int id, string name, object args) => GatewayFixture.Complete(call,
        [new { type = "function_call", call_id = $"call_{id}", name, arguments = JsonSerializer.Serialize(args) }]);

    private static JsonElement[] ToolFrames(string wire) => wire.Split('\n')
        .Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line[6..]))
        .Where(frame => frame.GetProperty("type").GetString()!.StartsWith("tool-", StringComparison.Ordinal)).ToArray();

    private static void AssertActivity(JsonElement frame, string type, string callId)
    {
        Assert.Equal(type, frame.GetProperty("type").GetString());
        Assert.Equal(callId, frame.GetProperty("toolCallId").GetString());
        Assert.True(frame.GetProperty("providerExecuted").GetBoolean());
        Assert.True(frame.GetProperty("dynamic").GetBoolean());
    }

    private static JsonElement ReadOutput(GatewayCall call, int id)
    {
        var output = Assert.Single(call.Body.GetProperty("input").EnumerateArray(), item =>
            item.GetProperty("type").GetString() == "function_call_output" && item.GetProperty("call_id").GetString() == $"call_{id}");
        using var result = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        Assert.False(result.RootElement.GetProperty("isError").GetBoolean());
        return result.RootElement.Clone();
    }

    private static void AssertSkill(JsonElement skill, string name, string id, string description)
    {
        Assert.Equal(3, skill.EnumerateObject().Count());
        Assert.Equal(name, skill.GetProperty("name").GetString());
        Assert.Equal(id, skill.GetProperty("skill_id").GetString());
        Assert.Equal(description, skill.GetProperty("description").GetString());
    }

    private static void AssertTool(JsonElement tool, string name, string[] fields)
    {
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal(name, tool.GetProperty("name").GetString());
        Assert.NotEmpty(tool.GetProperty("description").GetString()!);
        var schema = tool.GetProperty("parameters");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(fields, schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(fields, schema.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.All(fields, field => Assert.Equal("string", schema.GetProperty("properties").GetProperty(field).GetProperty("type").GetString()));
    }
}

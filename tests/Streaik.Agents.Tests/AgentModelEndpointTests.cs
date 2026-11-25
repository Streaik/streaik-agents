using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Streaik.Agents.Tests;

public sealed class AgentModelEndpointTests
{
    [Fact]
    public async Task Discovery_lists_only_summaries_and_retrieves_one_complete_public_definition()
    {
        const string definition = """
            {
              "name":"team/research%2F", "description":"Research summary", "instructions":"Answer with citations.",
              "owned_by":" Research ", "tags":["research"], "argumentHint":"A research question",
              "model":{"id":"openai/legacy"},
              "models":[{"id":"openai/first","type":"language","options":{"temperature":0.25},
                "providerMetadata":{"reasoning":{"effort":"low"},"opaque":[null,true,2]},
                "providerHeaders":{"OpenAI-Beta":"sample","x-openai-key":"provider-secret"}},
                {"id":"fireworks/routing","type":"decision"}],
              "responseFormat":{"type":"json"},"mcpServers":{"search":{"url":"/mcp/search","headers":{"Authorization":"mcp-secret","X-Mode":"test"}}},
              "mcpClient":{"policy":{"readOnlyHint":true}},"skills":[{"type":"skill_reference","skill_id":"research"}],
              "tools":[{"type":"web_search"}],"icons":[{"src":"icon.png"}],"plugins":[{"type":"base64","data":"AQ=="}],
              "evaluations":{"localEvaluator":{"nonEmpty":{"minLength":1}}},
              "transcription":{"id":"openai/transcribe"},"speech":{"model":{"id":"openai/speech"},"voice":"voice"},
              "futureAgentField":{"preserved":true}
            }
            """;
        var agent = JsonSerializer.Deserialize<AgentDefinition>(definition, JsonSerializerOptions.Web)!;
        await using var host = await TestAgentHost.StartAsync(extraSource: new FixedSource(agent, new AgentDefinition
        {
            Name = "team/research/", Model = new AgentModel { Id = "openai/plain" }
        }));

        var list = await host.Client.GetFromJsonAsync<JsonElement>("/v1/models");
        Assert.Equal("list", list.GetProperty("object").GetString());
        Assert.Equal(2, list.EnumerateObject().Count());
        var entries = list.GetProperty("data").EnumerateArray().ToArray();
        var ids = entries.Select(entry => entry.GetProperty("id").GetString()).ToArray();
        Assert.Equal(ids.Order(StringComparer.OrdinalIgnoreCase), ids);
        Assert.All(entries, entry => Assert.False(entry.TryGetProperty("agent", out _)));
        var summary = entries.Single(entry => entry.GetProperty("id").GetString() == agent.Name);
        Assert.Equal(new[] { "created", "description", "id", "name", "object", "owned_by", "tags" },
            summary.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("model", summary.GetProperty("object").GetString());
        Assert.Equal("Research", summary.GetProperty("owned_by").GetString());
        Assert.Equal(new[] { "research", "real-time" }, summary.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        var single = await host.Client.GetFromJsonAsync<JsonElement>("/v1/models/TEAM%2Fresearch%252F?query=ignored");
        Assert.Equal(agent.Name, single.GetProperty("id").GetString());
        Assert.Equal("research", Assert.Single(single.GetProperty("tags").EnumerateArray()).GetString());
        var published = single.GetProperty("agent");
        using var source = JsonDocument.Parse(definition);
        foreach (var property in source.RootElement.EnumerateObject().Where(property => property.Name is not ("models" or "mcpServers" or "speech")))
            Assert.True(JsonElement.DeepEquals(property.Value, published.GetProperty(property.Name)), property.Name);
        var model = published.GetProperty("models")[0];
        Assert.Equal("openai/first", model.GetProperty("id").GetString());
        Assert.Equal(0.25, model.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal("low", model.GetProperty("providerMetadata").GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("sample", model.GetProperty("providerHeaders").GetProperty("OpenAI-Beta").GetString());
        Assert.Equal("fireworks/routing", published.GetProperty("models")[1].GetProperty("id").GetString());
        Assert.Equal("openai/speech", published.GetProperty("speech").GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("test", published.GetProperty("mcpServers").GetProperty("search").GetProperty("headers").GetProperty("X-Mode").GetString());
        Assert.DoesNotContain("provider-secret", single.GetRawText());
        Assert.DoesNotContain("mcp-secret", single.GetRawText());
        Assert.Equal("provider-secret", agent.Models![0]!.ProviderHeaders!["x-openai-key"]);

        var slash = await host.Client.GetFromJsonAsync<JsonElement>("/v1/models/team%2Fresearch%2F");
        Assert.Equal("team/research/", slash.GetProperty("id").GetString());
        using var missing = await host.Client.GetAsync("/v1/models/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var error = (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
        Assert.Equal("model", error.GetProperty("param").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Empty(host.Gateway.Calls);
    }

    [Fact]
    public async Task OpenApi_exposes_only_the_endpoint_specific_workflow_metadata_and_documents_discovery()
    {
        await using var host = await TestAgentHost.StartAsync();
        var doc = await host.Client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = doc.GetProperty("paths");
        Assert.True(paths.GetProperty("/v1/models").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/v1/models/{modelId}").TryGetProperty("get", out _));
        Check("/api/chat", "workflowMetadata", "workflow_metadata");
        Check("/v1/responses", "workflow_metadata", "workflowMetadata");

        void Check(string path, string correct, string removed)
        {
            var schema = paths.GetProperty(path).GetProperty("post").GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema");
            if (schema.TryGetProperty("$ref", out var reference))
                schema = doc.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
            var fields = schema.GetProperty("properties");
            Assert.True(fields.TryGetProperty(correct, out _));
            Assert.False(fields.TryGetProperty(removed, out _));
        }
    }

    private sealed class FixedSource(params AgentDefinition[] agents) : IAgentSource
    {
        public Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentDefinition>>(agents);
    }
}

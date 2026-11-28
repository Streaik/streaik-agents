using System.Net;
using System.Text.Json.Nodes;

namespace Streaik.Agents.Tests;

public sealed class AgentOpenApiTests
{
    [Fact]
    public async Task Scalar_groups_the_same_operations_by_family_and_capability()
    {
        await using var host = await TestAgentHost.StartAsync();
        var family = (await host.Client.GetFromJsonAsync<JsonNode>("/openapi/v1.json"))!;
        var capability = (await host.Client.GetFromJsonAsync<JsonNode>("/openapi/capabilities.json"))!;

        Assert.Equal(new[] { "Anthropic", "OpenAI", "Vercel" }, family["tags"]!.AsArray()
            .Select(tag => tag!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Null(family["x-tagGroups"]);

        var groups = capability["x-tagGroups"]!.AsArray();
        Assert.Equal(new[] { "Chat", "LLM" }, groups.Select(group => group!["name"]!.GetValue<string>()));
        Assert.Equal(new[] { "Chat /v1", "Chat /api" }, Strings(groups[0]!["tags"]!));
        Assert.Equal("LLM /v1", Assert.Single(Strings(groups[1]!["tags"]!)));
        var displayNames = capability["tags"]!.AsArray().ToDictionary(tag => tag!["name"]!.GetValue<string>(),
            tag => tag!["x-displayName"]!.GetValue<string>());
        Assert.Equal(3, displayNames.Count);
        Assert.Equal("/v1", displayNames["Chat /v1"]);
        Assert.Equal("/api", displayNames["Chat /api"]);
        Assert.Equal("/v1", displayNames["LLM /v1"]);

        (string Path, string Method, string FamilyTag, string CapabilityTag)[] operations =
        [
            ("/v1/responses", "post", "OpenAI", "Chat /v1"),
            ("/v1/chat/completions", "post", "OpenAI", "Chat /v1"),
            ("/v1/messages", "post", "Anthropic", "Chat /v1"),
            ("/api/chat", "post", "Vercel", "Chat /api"),
            ("/v1/models", "get", "OpenAI", "LLM /v1"),
            ("/v1/models/{modelId}", "get", "OpenAI", "LLM /v1")
        ];
        Assert.Equal(operations.Length, family["paths"]!.AsObject().Count);
        foreach (var (path, method, familyTag, capabilityTag) in operations)
        {
            var familyOperation = family["paths"]![path]![method]!;
            // Without a summary, Scalar uses the route as the operation title, like the gateway.
            Assert.Null(familyOperation["summary"]);
            AssertTagAndRemove(familyOperation, familyTag);
            AssertTagAndRemove(capability["paths"]![path]![method]!, capabilityTag);
        }
        Assert.True(JsonNode.DeepEquals(family["paths"], capability["paths"]), "Only operation tags should differ.");
        Assert.True(JsonNode.DeepEquals(family["components"], capability["components"]), "Both views should use the same components.");

        var scalar = await host.Client.GetStringAsync("/scalar");
        Assert.Contains("By API family", scalar);
        Assert.Contains("By capability", scalar);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("capabilities")]
    public async Task Both_documents_use_the_protocol_specific_workflow_fields(string documentName)
    {
        await using var host = await TestAgentHost.StartAsync();
        var document = (await host.Client.GetFromJsonAsync<JsonNode>($"/openapi/{documentName}.json"))!;
        var responses = document["paths"]!["/v1/responses"]!["post"]!;
        var chat = document["paths"]!["/api/chat"]!["post"]!;

        AssertWorkflowFields(responses, "workflow_type", "workflow_metadata", "workflowType", "workflowMetadata");
        AssertWorkflowFields(chat, "workflowType", "workflowMetadata", "workflow_type", "workflow_metadata");
        Assert.NotNull(chat["responses"]!["200"]!["content"]!["text/event-stream"]);
        Assert.NotNull(responses["responses"]!["200"]!["content"]!["application/json"]);
        Assert.NotNull(responses["responses"]!["200"]!["content"]!["text/event-stream"]);
        Assert.NotNull(document["components"]!["securitySchemes"]!["providerBearer"]);
        foreach (var (path, requiresMaxTokens) in new[] { ("/v1/chat/completions", false), ("/v1/messages", true) })
        {
            var operation = document["paths"]![path]!["post"]!;
            var request = operation["requestBody"]!["content"]!["application/json"]!;
            var examples = request["examples"]!.AsObject();
            Assert.Single(examples);
            var example = examples["namedAgent"]!["value"]!;
            Assert.Equal("GreetingAgent", example["model"]!.GetValue<string>());
            Assert.Equal(requiresMaxTokens, example["max_tokens"] is not null);
            var responsesContent = operation["responses"]!["200"]!["content"]!;
            Assert.NotNull(responsesContent["application/json"]);
            Assert.NotNull(responsesContent["text/event-stream"]);
        }

        void AssertWorkflowFields(JsonNode operation, string type, string metadata, string alternateType, string alternateMetadata)
        {
            var request = operation["requestBody"]!["content"]!["application/json"]!;
            var example = request["examples"]!["workflow"]!["value"]!.AsObject();
            Assert.Equal("sequential", example[type]!.GetValue<string>());
            Assert.False(example.ContainsKey(alternateType));

            var schema = request["schema"]!;
            if (schema["$ref"] is { } reference) schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!;
            var properties = schema["properties"]!.AsObject();
            Assert.False(properties.ContainsKey(alternateType));
            Assert.False(properties.ContainsKey(alternateMetadata));
            Assert.True(properties.ContainsKey(metadata));
            Assert.Equal("sequential", properties[type]!["default"]!.GetValue<string>());
            Assert.Equal(new[] { "sequential", "concurrent", "handoff", "groupchat", "magentic", "decisions" }, Strings(properties[type]!["enum"]!));
        }
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/openapi/capabilities.json")]
    [InlineData("/scalar")]
    public async Task Azure_auth_does_not_expose_the_reference(string path)
    {
        await using var host = await TestAgentHost.StartAsync(azure: true);
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void AssertTagAndRemove(JsonNode operation, string tag)
    {
        Assert.Equal(tag, Assert.Single(Strings(operation["tags"]!)));
        operation.AsObject().Remove("tags");
    }

    private static IEnumerable<string> Strings(JsonNode array) => array.AsArray().Select(item => item!.GetValue<string>());
}

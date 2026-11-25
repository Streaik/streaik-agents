using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Agents.AzureAuth;
using Streaik.Agents.Blob;
using Streaik.Agents.Json;

namespace Streaik.Agents.Tests;

public sealed class AgentCatalogTests
{
    [Fact]
    public async Task Bundled_agents_keep_their_names_instructions_and_first_model()
    {
        var definitions = await new JsonAgentSource().GetAgentsAsync();
        Assert.Equal(new (string?, string?)[]
        {
            ("AnthropicAgent", "anthropic/claude-sonnet-5-5"),
            ("CerebrasAgent", "cerebras/gpt-oss-120b"),
            ("GoogleAgent", "google/gemini-omni-1.2-flash"),
            ("MistralAgent", "mistral/mistral-medium-3-5"),
            ("OpenAIAgent", "openai/gpt-6.1-sol"),
            ("SpaceXAIAgent", "spacexai/grok-4.7")
        }, definitions.Select(agent => (agent.Name, agent.Model?.Id)));
        Assert.All(definitions, agent =>
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Description));
            Assert.False(string.IsNullOrWhiteSpace(agent.Instructions));
            Assert.Equal("v1/responses", agent.Transport);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_auth_hosts_can_run_a_bundled_agent_without_storage(bool azure)
    {
        await using var host = await TestAgentHost.StartAsync(azure);
        using var request = TestAgentHost.Post("/v1/responses", new { model = "openaiagent", input = "Hi" },
            azure ? TestAgentHost.Token("alice") : "fixture-key");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(host.Gateway.Calls);
        Assert.Equal("openai/gpt-6.1-sol", call.Body.GetProperty("model").GetString());
        Assert.Equal("/base/v1/responses", call.Endpoint.AbsolutePath);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("UseDevelopmentStorage=true", "", false)]
    [InlineData("UseDevelopmentStorage=true", "agents", true)]
    public void Azure_registers_json_and_only_enables_blob_when_configured(string? connection, string? container, bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BlobAgents:ConnectionString"] = connection,
            ["BlobAgents:ContainerName"] = container
        }).Build();
        var services = new ServiceCollection();
        services.AddAzureAgentSources(config.GetSection("BlobAgents"));
        using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentSource>().ToArray();
        Assert.IsType<JsonAgentSource>(sources[0]);
        if (enabled) Assert.IsType<BlobAgentSource>(Assert.Single(sources.Skip(1)));
        else Assert.Single(sources);
    }

    [Fact]
    public async Task Blob_definitions_use_the_same_importer_and_override_bundled_names_until_cache_expires()
    {
        var clock = new TestClock();
        var container = new FixtureContainer();
        container.Files["tenant/reference/Override/aGeNt.JsOn"] = """
            {
              "name":"OPENAIAGENT", "description":"Storage definition", "instructions":"Reply from the blob.",
              "models":[null,{}, {"id":"openai/from-blob", "providerMetadata":{"tools":[]}}, {"id":"openai/other"}],
              "speech": {"model":{"id":"openai/speech"}}, "transport":"responses"
            }
            """;
        container.Files["tenant/reference/Extra/Agent.json"] = """
            {"name":"Extra", "model":{"id":"openai/preferred"}, "models":[{"id":"openai/ignored"}]}
            """;
        container.Files["tenant/reference/ignored.json"] = "This must not be downloaded.";
        container.Files["elsewhere/Agent.json"] = "This must not be downloaded.";
        var catalog = new AgentCatalog([new JsonAgentSource(), new BlobAgentSource(container, @"\tenant\reference\", clock)]);

        var resolved = await catalog.ResolveAsync(["OpenAIAgent", "Extra", "GoogleAgent"]);
        Assert.Equal(new[] { "openai/from-blob", "openai/preferred", "google/gemini-omni-1.2-flash" },
            resolved.Select(agent => agent.Model!.Id));
        Assert.Equal("Storage definition", resolved[0].Description);
        Assert.Equal("Reply from the blob.", resolved[0].Instructions);
        Assert.Equal("responses", resolved[0].Transport);
        Assert.Equal("tenant/reference", container.Prefix);
        Assert.Equal(1, container.Listings);
        Assert.Equal(2, container.Downloads);

        container.Files.Remove("tenant/reference/Override/aGeNt.JsOn");
        Assert.Same(resolved[0], Assert.Single(await catalog.ResolveAsync(["openaiagent"])));
        Assert.Equal(1, container.Listings);
        clock.Now = clock.Now.AddMinutes(10);
        Assert.Equal("openai/gpt-6.1-sol", Assert.Single(await catalog.ResolveAsync(["openaiagent"])).Model!.Id);
        Assert.Equal(2, container.Listings);
    }

    [Fact]
    public async Task Missing_blob_container_leaves_the_bundled_catalog_available()
    {
        var container = new FixtureContainer { ContainerExists = false };
        var catalog = new AgentCatalog([new JsonAgentSource(), new BlobAgentSource(container)]);
        Assert.Equal("MistralAgent", Assert.Single(await catalog.ResolveAsync(["mistralagent"])).Name);
        Assert.Equal(0, container.Listings);
    }

    [Fact]
    public async Task Inline_runs_do_not_read_storage_and_named_runs_sanitize_storage_failures()
    {
        await using var host = await TestAgentHost.StartAsync(azure: true, extraSource: new FailingSource());
        const string path = "/v1/responses";
        var token = TestAgentHost.Token("alice");
        using var named = TestAgentHost.Post(path, new { model = "OpenAIAgent", input = "Hi" }, token);
        using var namedResponse = await host.Client.SendAsync(named);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, namedResponse.StatusCode);
        var error = await namedResponse.Content.ReadAsStringAsync();
        Assert.Contains("agent_catalog_unavailable", error);
        Assert.DoesNotContain("private storage error", error);
        Assert.Empty(host.Gateway.Calls);

        using var inline = TestAgentHost.Post(path, new { agent = new { name = "Inline", model = new { id = "openai/test" } }, input = "Hi" }, token);
        using var inlineResponse = await host.Client.SendAsync(inline);
        Assert.Equal(HttpStatusCode.OK, inlineResponse.StatusCode);
        Assert.Single(host.Gateway.Calls);
    }

    private sealed class FailingSource : IAgentSource
    {
        public Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("private storage error");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureContainer : BlobContainerClient
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public bool ContainerExists { get; init; } = true;
        public int Listings { get; private set; }
        public int Downloads { get; private set; }
        public string? Prefix { get; private set; }

        public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken)
            => Task.FromResult(Response.FromValue(ContainerExists, null!));

        public override AsyncPageable<BlobItem> GetBlobsAsync(BlobTraits traits, BlobStates states, string? prefix,
            CancellationToken cancellationToken)
        {
            Listings++;
            Prefix = prefix;
            var items = Files.Keys.Where(name => prefix is null || name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => BlobsModelFactory.BlobItem(name: name)).ToArray();
            return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, null, null!)]);
        }

        public override BlobClient GetBlobClient(string blobName)
        {
            Downloads++;
            return new FixtureBlob(Files[blobName]);
        }
    }

    private sealed class FixtureBlob(string content) : BlobClient
    {
        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken)
            => Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(content)), null!));
    }
}

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Streaik.Agents.Blob;

public sealed class BlobAgentSource(BlobContainerClient container, string? prefix = null, TimeProvider? clock = null)
    : IAgentSource
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string? _prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Replace('\\', '/').Trim('/');
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile Snapshot? _cache;

    public async Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache is { } cached && _clock.GetUtcNow() < cached.ExpiresAt) return cached.Agents;

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } current && _clock.GetUtcNow() < current.ExpiresAt) return current.Agents;
            var agents = await LoadAsync(cancellationToken);
            _cache = new Snapshot(agents, _clock.GetUtcNow().Add(CacheDuration));
            return agents;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task<IReadOnlyList<AgentDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!await container.ExistsAsync(cancellationToken)) return [];

        var agents = new List<AgentDefinition>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, _prefix, cancellationToken))
        {
            if (!blob.Name.EndsWith("Agent.json", StringComparison.OrdinalIgnoreCase)) continue;
            var download = await container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
            using var stream = download.Value.Content.ToStream();
            if (AgentDefinitionImport.Read(stream) is { } agent) agents.Add(agent);
        }
        return agents.AsReadOnly();
    }

    private sealed record Snapshot(IReadOnlyList<AgentDefinition> Agents, DateTimeOffset ExpiresAt);
}

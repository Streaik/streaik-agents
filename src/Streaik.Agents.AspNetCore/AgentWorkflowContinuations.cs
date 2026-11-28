using System.Security.Cryptography;
using System.Text.Json;

namespace Streaik.Agents.AspNetCore;

internal sealed record WorkflowTarget(Agent Definition, AgentModel Model);
internal sealed record WorkflowContinuation(AgentEndpointProtocol Protocol, string Owner, bool SingleProvider,
    WorkflowTarget[] Targets, AgentWorkflow Workflow, JsonElement Request, WorkflowCheckpoint Checkpoint,
    WorkflowPendingCall[] Calls);

// Keep checkpoint storage independent of the HTTP request and the framework's live run objects.
internal interface IAgentWorkflowContinuationStore
{
    ValueTask SaveAsync(string token, byte[] state, CancellationToken ct);
    ValueTask<byte[]?> FindAsync(string token, CancellationToken ct);
    ValueTask<bool> ConsumeAsync(string token, CancellationToken ct);
}

internal sealed class InMemoryAgentWorkflowContinuationStore(TimeProvider clock) : IAgentWorkflowContinuationStore, IDisposable
{
    private readonly Dictionary<string, (byte[] State, DateTimeOffset Expiry)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private ITimer? _timer;

    public ValueTask SaveAsync(string token, byte[] state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.Length > 2 * 1024 * 1024)
            throw new AgentRequestException("The workflow checkpoint exceeds the size limit.", "workflow_checkpoint_too_large");
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            if (_entries.Count >= 64)
                throw new AgentRequestException("The workflow continuation store is full. Try again later.", "workflow_checkpoint_capacity");
            _entries.Add(token, (state, now.AddMinutes(10)));
            _timer ??= clock.CreateTimer(_ => { lock (_gate) Prune(clock.GetUtcNow()); }, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> FindAsync(string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            return ValueTask.FromResult(_entries.TryGetValue(token, out var state) ? state.State : null);
        }
    }

    public ValueTask<bool> ConsumeAsync(string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            return ValueTask.FromResult(_entries.Remove(token));
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (token, entry) in _entries)
            if (entry.Expiry <= now) _entries.Remove(token);
    }

    public void Dispose() => _timer?.Dispose();
}

internal sealed class AgentWorkflowOwner
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string Identify(string? subject, IEnumerable<IReadOnlyDictionary<string, string>> credentials)
    {
        if (subject is not null) return "subject:" + subject;
        var headers = credentials.SelectMany(values => values.Select(pair => (Name: pair.Key.ToLowerInvariant(), pair.Value)))
            .Distinct().OrderBy(pair => pair.Name, StringComparer.Ordinal).ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => new[] { pair.Name, pair.Value }).ToArray();
        if (headers.Length == 0)
            throw new AgentRequestException("Workflow continuation requires an authenticated caller or provider credentials.");
        return Convert.ToHexString(HMACSHA256.HashData(_key, JsonSerializer.SerializeToUtf8Bytes(headers)));
    }
}

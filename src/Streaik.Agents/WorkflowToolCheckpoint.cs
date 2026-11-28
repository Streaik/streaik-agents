using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;

namespace Streaik.Agents;

internal sealed record WorkflowPendingCall(RequestPortInfo Port, string RequestId, FunctionCallContent Call);
internal sealed record WorkflowCheckpoint(CheckpointInfo Info, CheckpointInfo? Parent, JsonElement State);

// The engine writes at superstep boundaries. Only the last snapshot is needed for continuation;
// discard earlier supersteps rather than accumulating them for the lifetime of the workflow.
internal sealed class WorkflowCheckpointBuffer(WorkflowCheckpoint? initial = null) : ICheckpointStore<JsonElement>
{
    public WorkflowCheckpoint? Latest { get; private set; } = initial;

    public ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
        if (value.GetRawText().Length > 1024 * 1024)
            throw new AgentRequestException("The workflow checkpoint exceeds the size limit.", "workflow_checkpoint_too_large");
        Latest = new(info, parent, value);
        return ValueTask.FromResult(info);
    }

    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null) =>
        ValueTask.FromResult<IEnumerable<CheckpointInfo>>(Latest is { } checkpoint && checkpoint.Info.SessionId == sessionId
            && (withParent is null || withParent.Equals(checkpoint.Parent)) ? [checkpoint.Info] : []);

    public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key) =>
        Latest is { } checkpoint && sessionId == checkpoint.Info.SessionId && key.Equals(checkpoint.Info)
            ? ValueTask.FromResult(checkpoint.State) : throw new InvalidOperationException("The workflow checkpoint was not found.");
}

internal sealed class WorkflowToolExchange(WorkflowCheckpoint? checkpoint = null,
    IReadOnlyList<WorkflowPendingCall>? answered = null, IReadOnlyDictionary<string, FunctionResultContent>? results = null)
{
    public WorkflowCheckpointBuffer Checkpoints { get; } = new(checkpoint);
    public IReadOnlyList<WorkflowPendingCall> Answered { get; } = answered ?? [];
    public IReadOnlyDictionary<string, FunctionResultContent>? Results { get; } = results;
    public Dictionary<string, WorkflowPendingCall> Pending { get; } = new(StringComparer.Ordinal);
}

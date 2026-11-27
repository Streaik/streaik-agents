using Microsoft.Extensions.AI;
using Streaik.Tools;

namespace Streaik.Agents;

// Deliberately not a FunctionCallContent or FunctionResultContent: the host already owns execution.
internal sealed class HostedToolActivityContent(HostedToolActivity activity) : AIContent
{
    public HostedToolActivity Activity { get; } = activity;
}

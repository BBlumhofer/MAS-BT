using Microsoft.Extensions.Logging;
using MAS_BT.Core;

namespace MAS_BT.Nodes.Core;

/// <summary>
/// ForceFailure - Gibt immer Failure zurück
/// Wird verwendet um einen Branch gezielt zu beenden
/// </summary>
public class ForceFailureNode : BTNode
{
    public ForceFailureNode() : base("ForceFailure")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("ForceFailure: Returning Failure");
        await Task.CompletedTask;
        return NodeStatus.Failure;
    }
}

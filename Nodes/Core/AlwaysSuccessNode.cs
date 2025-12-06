using Microsoft.Extensions.Logging;
using MAS_BT.Core;

namespace MAS_BT.Nodes.Core;

/// <summary>
/// AlwaysSuccess - Gibt immer Success zurück
/// Wird verwendet um einen Fallback-Branch erfolgreich zu beenden
/// </summary>
public class AlwaysSuccessNode : BTNode
{
    public AlwaysSuccessNode() : base("AlwaysSuccess")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("AlwaysSuccess: Returning Success");
        await Task.CompletedTask;
        return NodeStatus.Success;
    }
}

using System;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;

namespace MAS_BT.Nodes.Core;

/// <summary>
/// ForceFailure - Führt genau ein Kind aus und liefert unabhängig vom Ergebnis Failure.
/// </summary>
public class ForceFailureNode : DecoratorNode
{
    public ForceFailureNode() : base("ForceFailure")
    {
    }

    public ForceFailureNode(string name) : base(name)
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        if (Child == null)
        {
            Logger.LogWarning("ForceFailure: No child attached, returning Failure");
            return NodeStatus.Failure;
        }

        try
        {
            var status = await Child.Execute();
            Logger.LogDebug("ForceFailure: Child finished with {Status}, forcing Failure", status);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ForceFailure: Child execution threw exception; forcing Failure");
        }

        return NodeStatus.Failure;
    }
}

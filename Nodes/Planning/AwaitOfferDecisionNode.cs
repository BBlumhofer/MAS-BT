using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// AwaitOfferDecision - stub: waits for accept/deny/require decision, uses context override if present.
/// </summary>
public class AwaitOfferDecisionNode : BTNode
{
    public int TimeoutMs { get; set; } = 2000;

    public AwaitOfferDecisionNode() : base("AwaitOfferDecision") {}

    public override Task<NodeStatus> Execute()
    {
        // Stub: read decision from context; default to ACCEPT
        var decision = Context.Get<string>("OfferDecision") ?? "ACCEPT";
        Context.Set("OfferDecision", decision);
        Logger.LogInformation("AwaitOfferDecision: decision={Decision} (timeoutMs={Timeout})", decision, TimeoutMs);
        return Task.FromResult(NodeStatus.Success);
    }
}

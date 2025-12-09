using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// ReceiveOfferMessage - stub: reads an offer decision from context; defaults to ACCEPT.
/// </summary>
public class ReceiveOfferMessageNode : BTNode
{
    public ReceiveOfferMessageNode() : base("ReceiveOfferMessage") {}

    public override Task<NodeStatus> Execute()
    {
        var decision = Context.Get<string>("OfferDecision") ?? "ACCEPT";
        Context.Set("OfferDecision", decision);
        Logger.LogInformation("ReceiveOfferMessage: decision={Decision}", decision);
        return Task.FromResult(NodeStatus.Success);
    }
}

using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// CalculateOffer - stub: computes placeholder offer data and stores in context.
/// </summary>
public class CalculateOfferNode : BTNode
{
    public CalculateOfferNode() : base("CalculateOffer") {}

    public override Task<NodeStatus> Execute()
    {
        var capability = Context.Get<string>("LastMatchedCapability") ?? string.Empty;
        var scheduleCap = Context.Get<string>("LastSchedulingCapability") ?? capability;
        var cost = Context.Get<double?>("SchedulingMarginalCost") ?? Context.Get<double?>("FeasibleCost") ?? 10.0;
        var duration = Context.Get<double?>("FeasibleDurationSec") ?? 120;
        var offer = new { Capability = scheduleCap, Cost = cost, DurationSec = duration, Confidence = 0.8, Status = "pending" };
        Context.Set("CurrentOffer", offer);
        Logger.LogInformation("CalculateOffer: offer for capability={Capability} cost={Cost} duration={Duration}", offer.Capability, offer.Cost, offer.DurationSec);
        return Task.FromResult(NodeStatus.Success);
    }
}

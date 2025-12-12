using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning.ProcessChain;

public class PlanCapabilityOfferNode : BTNode
{
    public double BaseCost { get; set; } = 50.0;
    public double CostPerMinute { get; set; } = 2.0;

    public PlanCapabilityOfferNode() : base("PlanCapabilityOffer") { }

    public override Task<NodeStatus> Execute()
    {
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        if (request == null)
        {
            Logger.LogError("PlanCapabilityOffer: capability request missing from context");
            return Task.FromResult(NodeStatus.Failure);
        }

        var now = DateTime.UtcNow;
        var setup = TimeSpan.FromMinutes(1);
        var cycle = TimeSpan.FromMinutes(5);
        var plannedStart = now.AddMinutes(2);

        var station = Context.Get<string>("config.Agent.ModuleName")
                      ?? Context.Get<string>("ModuleId")
                      ?? Context.AgentId
                      ?? "PlanningStation";

        var cost = BaseCost + (cycle.TotalMinutes * CostPerMinute);
        if (!string.IsNullOrEmpty(request.ProductId))
        {
            cost += request.ProductId.Length;
        }

        var rawOfferId = $"{station}-{Guid.NewGuid():N}";
        var offerId = rawOfferId.Length > 40 ? rawOfferId[..40] : rawOfferId;

        var plan = new CapabilityOfferPlan
        {
            OfferId = offerId,
            StationId = station,
            StartTimeUtc = plannedStart,
            SetupTime = setup,
            CycleTime = cycle,
            Cost = Math.Round(cost, 2)
        };

        Context.Set("Planning.CapabilityOffer", plan);
        Logger.LogInformation("PlanCapabilityOffer: planned start={Start} cost={Cost}", plannedStart, plan.Cost);
        return Task.FromResult(NodeStatus.Success);
    }
}

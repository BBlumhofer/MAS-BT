using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using AasSharpClient.Models.ProcessChain;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Nodes.Planning.ProcessChain;

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

        // Build an AAS OfferedCapability and CapabilityOfferPlan instead of anonymous JSON
        var station = Context.Get<string>("config.Agent.ModuleId")
                      ?? Context.Get<string>("ModuleId")
                      ?? Context.AgentId
                      ?? "PlanningStation";

        var now = DateTime.UtcNow;
        var setup = TimeSpan.Zero;
        var cycle = TimeSpan.FromSeconds(duration);

        var rawOfferId = $"{station}-{System.Guid.NewGuid():N}";
        var offerId = rawOfferId.Length > 40 ? rawOfferId[..40] : rawOfferId;

        var offeredCapability = new OfferedCapability("OfferedCapability");
        offeredCapability.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
        offeredCapability.Station.Value = new PropertyValue<string>(station);
        offeredCapability.MatchingScore.Value = new PropertyValue<double>(0.8);
        offeredCapability.SetEarliestScheduling(now, now.Add(cycle), setup, cycle);
        offeredCapability.SetCost(Math.Round(cost, 2));

        var action = new AasSharpClient.Models.Action(
            idShort: $"Action_{scheduleCap}",
            actionTitle: scheduleCap,
            status: ActionStatusEnum.OPEN,
            inputParameters: null,
            finalResultData: null,
            preconditions: null,
            skillReference: null,
            machineName: station);

        offeredCapability.AddAction(action);

        var plan = new CapabilityOfferPlan
        {
            OfferId = offerId,
            StationId = station,
            StartTimeUtc = now,
            SetupTime = setup,
            CycleTime = cycle,
            Cost = Math.Round(cost, 2),
            OfferedCapability = offeredCapability
        };

        Context.Set("CurrentOffer", offeredCapability);
        Context.Set("Planning.CapabilityOffer", plan);
        Logger.LogInformation("CalculateOffer: created OfferedCapability offerId={OfferId} capability={Capability} cost={Cost}", offerId, scheduleCap, plan.Cost);
        return Task.FromResult(NodeStatus.Success);
    }
}

using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using AasSharpClient.Models;
using ActionModel = AasSharpClient.Models.Action;
using StepModel = AasSharpClient.Models.Step;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// DispatchScheduledSteps - stub: triggers skill request dispatch for booked steps.
/// </summary>
public class DispatchScheduledStepsNode : BTNode
{
    public DispatchScheduledStepsNode() : base("DispatchScheduledSteps") {}

    public override Task<NodeStatus> Execute()
    {
        var action = Context.Get<ActionModel>("CurrentPlanAction");
        var step = Context.Get<StepModel>("CurrentPlanStep");
        var status = Context.Get<string>("OfferStatus") ?? "pending";
        var transportOk = Context.Get<bool?>("TransportAccepted") ?? true;
        var machineReady = Context.Get<bool?>("MachineReady") ?? true;
        var resourceAvailable = Context.Get<bool?>("ResourceAvailable") ?? true;
        var referenceTime = Context.Get<DateTime?>("SchedulingReferenceTimeUtc") ?? DateTime.UtcNow;
        var schedulingAllowed = step?.Scheduling?.AllowedToStartStep(referenceTime) ?? true;

        if (action != null && status is "booked" and not null && transportOk && machineReady && resourceAvailable && schedulingAllowed)
        {
            Logger.LogInformation("DispatchScheduledSteps: dispatching booked action {ActionId}", action.IdShort);
            Context.Set("DispatchReady", true);
        }
        else
        {
            Context.Set("DispatchReady", false);
            Logger.LogDebug(
                "DispatchScheduledSteps: not dispatching (status={Status}, transportOk={Transport}, ready={Ready}, available={Avail}, schedulingOk={Scheduling})",
                status,
                transportOk,
                machineReady,
                resourceAvailable,
                schedulingAllowed);
        }

        return Task.FromResult(NodeStatus.Success);
    }
}

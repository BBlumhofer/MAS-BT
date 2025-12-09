using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using AasSharpClient.Models;
using ActionModel = AasSharpClient.Models.Action;
using StepModel = AasSharpClient.Models.Step;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// DeriveStepFromCapability - stub: creates a step candidate based on matched capability and current plan action.
/// Stores DerivedStep in context for downstream checks.
/// </summary>
public class DeriveStepFromCapabilityNode : BTNode
{
    public string RefusalReason { get; set; } = "step_derivation_failed";

    public DeriveStepFromCapabilityNode() : base("DeriveStepFromCapability") {}

    public override Task<NodeStatus> Execute()
    {
        var matched = Context.Get<string>("MatchedCapability") ?? string.Empty;
        var action = Context.Get<ActionModel>("CurrentPlanAction");
        var step = Context.Get<StepModel>("CurrentPlanStep");

        if (action == null)
        {
            Logger.LogError("DeriveStepFromCapability: no CurrentPlanAction in context");
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }

        // Stub: use existing step if available; otherwise create a lightweight one
        var derivedStep = step ?? new StepModel(
            "StepFrom" + action.IdShort,
            matched,
            StepStatusEnum.OPEN,
            action,
            action.MachineName.Value.Value?.ToString() ?? string.Empty,
            new SchedulingContainer(DateTime.UtcNow.ToString("o"), DateTime.UtcNow.AddMinutes(1).ToString("o"), "PT0S", "PT60S"),
            "Enterprise",
            "WorkCenter");

        Context.Set("DerivedStep", derivedStep);
        Context.Set("DerivedCapability", matched);
        Logger.LogInformation("DeriveStepFromCapability: derived step {StepId} for capability {Capability}", derivedStep.IdShort, matched);
        return Task.FromResult(NodeStatus.Success);
    }
}

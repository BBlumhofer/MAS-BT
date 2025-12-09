using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SelectSchedulableAction - picks the first action whose scheduling window has started (now or earlier)
/// and is still in a schedulable state, then marks it as the current dispatch candidate.
/// </summary>
public class SelectSchedulableActionNode : BTNode
{
    /// <summary>
    /// Optional horizon in seconds that allows selecting actions slightly before their scheduled start.
    /// </summary>
    public double LeadTimeSeconds { get; set; } = 0;

    public SelectSchedulableActionNode() : base("SelectSchedulableAction") {}

    public override Task<NodeStatus> Execute()
    {
        var plan = Context.Get<ProductionPlan>("ProductionPlan");
        if (plan == null || plan.Steps.Count == 0)
        {
            Logger.LogWarning("SelectSchedulableAction: missing ProductionPlan or no steps available");
            Context.Set("DispatchReady", false);
            return Task.FromResult(NodeStatus.Failure);
        }

        var referenceTime = Context.Get<DateTime?>("SchedulingReferenceTimeUtc") ?? DateTime.UtcNow;
        var horizon = referenceTime.AddSeconds(LeadTimeSeconds);

        var candidate = plan.Steps
            .SelectMany(step => step.Actions.Select(action => new { step, action }))
            .Where(item => IsSchedulable(item.action))
            .Select(item => new
            {
                item.step,
                item.action,
                Start = item.step.Scheduling?.GetStartDateTime() ?? DateTime.MinValue
            })
            .Where(item => item.Start <= horizon)
            .OrderBy(item => item.Start)
            .FirstOrDefault();

        if (candidate == null)
        {
            Context.Set("DispatchReady", false);
            return Task.FromResult(NodeStatus.Failure);
        }

        Context.Set("CurrentPlanStep", candidate.step);
        Context.Set("CurrentPlanAction", candidate.action);
        var actionTitle = candidate.action.ActionTitle.Value.Value?.ToString() ?? candidate.action.IdShort;
        Context.Set("ActionTitle", actionTitle);
        var machineName = candidate.action.MachineName.Value.Value?.ToString()
            ?? candidate.step.Station?.Value?.Value?.ToString()
            ?? Context.Get<string>("MachineName")
            ?? string.Empty;
        Context.Set("MachineName", machineName);
        Context.Set("DispatchReady", true);

        Logger.LogInformation("SelectSchedulableAction: selected {ActionId} (start={Start}) for module {Module}",
            candidate.action.IdShort,
            candidate.Start,
            machineName);

        return Task.FromResult(NodeStatus.Success);
    }

    private static bool IsSchedulable(AasSharpClient.Models.Action action)
    {
        return action.State is ActionStatusEnum.OPEN
            or ActionStatusEnum.PLANNED
            or ActionStatusEnum.PRECONDITION_FAILED;
    }
}

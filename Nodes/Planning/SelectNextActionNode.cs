using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SelectNextAction - picks the first non-completed action from the ProductionPlan and stores it in context.
/// </summary>
public class SelectNextActionNode : BTNode
{
    public SelectNextActionNode() : base("SelectNextAction")
    {
    }

    public override Task<NodeStatus> Execute()
    {
        var plan = Context.Get<ProductionPlan>("ProductionPlan");
        if (plan == null)
        {
            Logger.LogError("SelectNextAction: No ProductionPlan in context");
            return Task.FromResult(NodeStatus.Failure);
        }

        var nextAction = plan.Steps
            .SelectMany(s => s.Actions.Select(a => (Step: s, Action: a)))
            .FirstOrDefault(pair => pair.Action.State is ActionStatusEnum.OPEN or ActionStatusEnum.PLANNED or ActionStatusEnum.PRECONDITION_FAILED);

        if (nextAction.Action == null)
        {
            Logger.LogInformation("SelectNextAction: No pending actions found");
            Context.Set("CurrentPlanAction", null);
            Context.Set("CurrentPlanStep", null);
            return Task.FromResult(NodeStatus.Failure);
        }

        var conversationId = Guid.NewGuid().ToString();

        Context.Set("CurrentPlanAction", nextAction.Action);
        Context.Set("CurrentPlanStep", nextAction.Step);
        Context.Set("ConversationId", conversationId);
            Context.Set("MachineName", nextAction.Action.MachineName.GetText() ?? string.Empty);
            Context.Set("ActionTitle", nextAction.Action.ActionTitle.GetText() ?? string.Empty);

        Logger.LogInformation("SelectNextAction: Selected {ActionId} on step {StepId} for machine {Machine}",
            nextAction.Action.IdShort,
            nextAction.Step.IdShort,
            Context.Get<string>("MachineName"));

        return Task.FromResult(NodeStatus.Success);
    }
}

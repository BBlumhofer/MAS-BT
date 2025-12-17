using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using MAS_BT.Tools;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// ApplySkillResponse - updates ProductionPlan action state based on the latest SkillResponse message.
/// </summary>
public class ApplySkillResponseNode : BTNode
{
    public ApplySkillResponseNode() : base("ApplySkillResponse")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var plan = Context.Get<ProductionPlan>("ProductionPlan");
        var payload = Context.Get<string>("LastSkillResponsePayload");
        var currentAction = Context.Get<AasSharpClient.Models.Action>("CurrentPlanAction");
        var currentStep = Context.Get<Step>("CurrentPlanStep");

        if (plan == null || string.IsNullOrWhiteSpace(payload) || currentAction == null || currentStep == null)
        {
            Logger.LogWarning("ApplySkillResponse: Missing plan/response/action/step in context");
            return NodeStatus.Failure;
        }

        var actionState = ExtractActionState(payload);
        if (string.IsNullOrWhiteSpace(actionState))
        {
            Logger.LogWarning("ApplySkillResponse: No ActionState found in response");
            return NodeStatus.Failure;
        }

        if (!Enum.TryParse<ActionStatusEnum>(actionState, true, out var status))
        {
            // map common execution states
            status = actionState.ToUpperInvariant() switch
            {
                "RUNNING" or "EXECUTING" => ActionStatusEnum.EXECUTING,
                "DONE" or "COMPLETED" => ActionStatusEnum.DONE,
                "ERROR" or "FAILURE" => ActionStatusEnum.ERROR,
                "PLANNED" => ActionStatusEnum.PLANNED,
                _ => ActionStatusEnum.OPEN
            };
        }

        try
        {
            currentAction.SetStatus(status);
            Logger.LogInformation("ApplySkillResponse: Set action {ActionId} status to {Status}", currentAction.IdShort, status);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ApplySkillResponse: Failed to set status for action {ActionId}", currentAction.IdShort);
        }

        // propagate to plan step/state via ProductionPlan helpers
        switch (status)
        {
            case ActionStatusEnum.DONE:
                plan.ReturnActionToCompleted(currentStep.IdShort, currentAction.IdShort);
                break;
            case ActionStatusEnum.ERROR:
                plan.ErrorAction(currentStep.IdShort, currentAction.IdShort);
                break;
            case ActionStatusEnum.EXECUTING:
                plan.ReturnActionToExecuting(currentStep.IdShort, currentAction.IdShort);
                break;
            case ActionStatusEnum.SUSPENDED:
                plan.ReturnActionToSuspended(currentStep.IdShort, currentAction.IdShort);
                break;
            default:
                break;
        }

        UpdateStepStateFromActions(currentStep, status);
        Logger.LogInformation("ApplySkillResponse: Step {StepId} state is {StepState}", currentStep.IdShort, currentStep.State);

        // Publish updated step snapshot after applying the response
        try
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            await MAS_BT.Services.StepUpdateBroadcaster.PublishStepAsync(Context, client, currentStep, "actionResponse");
        }
        catch
        {
            // best-effort
        }

        return NodeStatus.Success;
    }

    private static void UpdateStepStateFromActions(Step step, ActionStatusEnum latestStatus)
    {
        if (step == null)
        {
            return;
        }

        if (latestStatus == ActionStatusEnum.EXECUTING)
        {
            PromoteStepToExecuting(step);
            return;
        }

        if (latestStatus == ActionStatusEnum.DONE && step.Actions.All(a => a.State == ActionStatusEnum.DONE))
        {
            CompleteStep(step);
        }
    }

    private static void PromoteStepToExecuting(Step step)
    {
        if (step.State == StepStatusEnum.EXECUTING)
        {
            return;
        }

        if (step.State == StepStatusEnum.OPEN)
        {
            step.Schedule();
        }

        if (step.State == StepStatusEnum.PLANNED)
        {
            step.StartProduction();
        }
    }

    private static void CompleteStep(Step step)
    {
        if (step.State == StepStatusEnum.DONE)
        {
            return;
        }

        PromoteStepToExecuting(step);

        if (!step.EndProduction())
        {
            step.SetStatus(StepStatusEnum.DONE);
        }
    }

    private static string? ExtractActionState(string json)
    {
        var root = JsonFacade.Parse(json);
        if (root is not IDictionary<string, object?> dict)
        {
            return null;
        }

        if (JsonFacade.GetPath(dict, new[] { "interactionElements" }) is not IList<object?> elements)
        {
            return null;
        }

        foreach (var el in elements)
        {
            if (TryExtractActionStateFromElement(el, out var state) && !string.IsNullOrWhiteSpace(state))
            {
                return state;
            }
        }

        return null;
    }

    private static bool TryExtractActionStateFromElement(object? element, out string? state)
    {
        state = null;

        if (element is not IDictionary<string, object?> dict)
        {
            return false;
        }

        var idShort = dict.TryGetValue("idShort", out var idShortRaw)
            ? JsonFacade.ToStringValue(idShortRaw)
            : null;

        if (string.Equals(idShort, "ActionState", StringComparison.OrdinalIgnoreCase))
        {
            if (dict.TryGetValue("value", out var valueRaw))
            {
                state = JsonFacade.ToStringValue(valueRaw);
                if (!string.IsNullOrWhiteSpace(state))
                {
                    return true;
                }
            }
        }

        if (dict.TryGetValue("value", out var nested) && nested is IList<object?> nestedElements)
        {
            foreach (var sub in nestedElements)
            {
                if (TryExtractActionStateFromElement(sub, out state) && !string.IsNullOrWhiteSpace(state))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

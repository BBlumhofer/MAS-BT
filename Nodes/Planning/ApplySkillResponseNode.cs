using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using I40Sharp.Messaging;

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
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("interactionElements", out var elements))
            {
                return null;
            }

            foreach (var el in elements.EnumerateArray())
            {
                if (el.TryGetProperty("idShort", out var idShortProp) && idShortProp.GetString()?.Equals("ActionState", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (el.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        return val.GetString();
                    }
                }

                if (el.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sub in valueProp.EnumerateArray())
                    {
                        if (sub.TryGetProperty("idShort", out var subId) && subId.GetString()?.Equals("ActionState", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (sub.TryGetProperty("value", out var subVal) && subVal.ValueKind == JsonValueKind.String)
                            {
                                return subVal.GetString();
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}

using System;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using MAS_BT.Services;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SendSkillRequest - builds and publishes a SkillRequest message for the current plan action.
/// </summary>
public class SendSkillRequestNode : BTNode
{
    public string ModuleId { get; set; } = string.Empty;

    public SendSkillRequestNode() : base("SendSkillRequest")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("SendSkillRequest: MessagingClient missing in context");
            return NodeStatus.Failure;
        }

        var action = Context.Get<AasSharpClient.Models.Action>("CurrentPlanAction");
        var conversationId = Context.Get<string>("ConversationId") ?? Guid.NewGuid().ToString();
        var machineName = Context.Get<string>("MachineName") ?? ModuleId;

        if (action == null)
        {
            Logger.LogError("SendSkillRequest: No CurrentPlanAction in context");
            return NodeStatus.Failure;
        }

        var message = new SkillRequestMessage(
            $"{machineName}_Planning_Agent",
            "PlanningAgent",
            $"{machineName}_Execution_Agent",
            "ExecutionAgent",
            conversationId,
            action);
        var topic = TopicHelper.BuildTopic(Context, "SkillRequest");
        Logger.LogInformation("SendSkillRequest: publishing to topic {Topic}", topic);
        await message.PublishAsync(client, topic);

        // Update context for responses
        Context.Set("ConversationId", conversationId);
        Context.Set("RequestSender", $"{machineName}_Planning_Agent");
        Context.Set("RequestReceiver", $"{machineName}_Execution_Agent");
        Context.Set("DispatchReady", false);

        if (!action.StartProduction())
        {
            action.SetStatus(ActionStatusEnum.EXECUTING);
        }
        Logger.LogInformation("SendSkillRequest: Published SkillRequest for action {ActionId} on {Topic}", action.IdShort, topic);

        // Publish step update so dispatcher/execution UIs are informed about action/step state changes
        try
        {
            var plan = Context.Get<ProductionPlan>("ProductionPlan");
            Step? parentStep = null;
            if (plan != null)
            {
                parentStep = plan.Steps.FirstOrDefault(s => s.Actions.Contains(action));
            }

            await StepUpdateBroadcaster.PublishStepAsync(Context, client, parentStep ?? new Step("Unknown", "Unknown", StepStatusEnum.OPEN, (AasSharpClient.Models.Action?)null, string.Empty, new SchedulingContainer(string.Empty, string.Empty, string.Empty, string.Empty), string.Empty, string.Empty));
        }
        catch
        {
            // best-effort
        }
        return NodeStatus.Success;
    }
}

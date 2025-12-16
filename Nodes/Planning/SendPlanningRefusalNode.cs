using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning.ProcessChain;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SendPlanningRefusal - sends a REFUSAL to the requester/product agent.
/// </summary>
public class SendPlanningRefusalNode : BTNode
{
    public string ReceiverId { get; set; } = "{RequesterId}";
    public string Reason { get; set; } = "unspecified";

    public SendPlanningRefusalNode() : base("SendPlanningRefusal") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("SendPlanningRefusal: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var conv = Context.Get<string>("ConversationId") ?? System.Guid.NewGuid().ToString();
        var refusalReason = Context.Get<string>("RefusalReason") ?? Reason;
        var failureDetail = Context.Get<string>("CapabilityMatchmaking.FailureDetail");
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");

        var resolvedReceiver = ResolvePlaceholders(ReceiverId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(resolvedReceiver))
        {
            Logger.LogError("SendPlanningRefusal: ReceiverId unresolved/missing");
            return NodeStatus.Failure;
        }

        var refusal = new PlanningRefusalMessage(
            "PlanningAgent",
            "PlanningAgent",
            resolvedReceiver,
            "ProductAgent",
            conv,
            refusalReason,
            failureDetail);

        await refusal.PublishAsync(client, $"/Planning/Refusals/{resolvedReceiver}/");
        Logger.LogInformation("SendPlanningRefusal: published refusal to {Receiver} with reason {Reason}", resolvedReceiver, refusalReason);
        return NodeStatus.Success;
    }
}

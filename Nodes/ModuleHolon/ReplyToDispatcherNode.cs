using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.ModuleHolon;

public class ReplyToDispatcherNode : BTNode
{
    public ReplyToDispatcherNode() : base("ReplyToDispatcher") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var msg = Context.Get<I40Message>("PlanningResponse") ?? Context.Get<I40Message>("LastReceivedMessage");
        if (client == null || msg == null)
        {
            Logger.LogError("ReplyToDispatcher: missing client or message");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var conv = msg.Frame?.ConversationId;

        // Get original requester (Dispatcher) from stored context
        var receiverId = Context.Get<string>($"OriginalRequester_{conv}");
        
        if (string.IsNullOrWhiteSpace(receiverId))
        {
            Logger.LogError(
                "ReplyToDispatcher: no original requester found for conv {Conv}, cannot determine response topic",
                conv);
            return NodeStatus.Failure;
        }

        // Send response directly to the original requester using generic topic pattern
        var topic = $"/{ns}/{receiverId}/OfferedCapability/Response";

        await client.PublishAsync(msg, topic);
        Logger.LogInformation(
            "ReplyToDispatcher: sent response conv {Conv} to requester {ReceiverId} on {Topic}",
            msg.Frame?.ConversationId,
            receiverId,
            topic);
        return NodeStatus.Success;
    }
}

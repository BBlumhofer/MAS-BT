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

        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        if (request == null)
        {
            Logger.LogDebug("SendPlanningRefusal: no capability request context available, skipping refusal");
            return NodeStatus.Success;
        }

        // Guard: prevent duplicate refusals for the same conversation
        var alreadyRefusedKey = $"Planning.RefusalSent:{request.ConversationId}";
        if (Context.Get<bool>(alreadyRefusedKey))
        {
            Logger.LogDebug("SendPlanningRefusal: already sent refusal for conversation {Conv}, skipping duplicate", request.ConversationId);
            return NodeStatus.Success;
        }

        var refusalReason = Context.Get<string>("RefusalReason") ?? Reason;
        var failureDetail = Context.Get<string>("CapabilityMatchmaking.FailureDetail");
        
        // Resolve receiver ID: try placeholder resolution first, then fallback to request context
        var resolvedReceiver = ResolvePlaceholders(ReceiverId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(resolvedReceiver) || resolvedReceiver.Contains('{'))
        {
            resolvedReceiver = request.RequesterId ?? Context.Get<string>("RequesterId");
        }
        if (string.IsNullOrWhiteSpace(resolvedReceiver))
        {
            Logger.LogError("SendPlanningRefusal: ReceiverId unresolved/missing");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace");
        if (string.IsNullOrWhiteSpace(ns))
        {
            Logger.LogError("SendPlanningRefusal: missing namespace (config.Namespace/Namespace)");
            return NodeStatus.Failure;
        }

        var conversationId = request?.ConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            conversationId = Context.Get<string>("ConversationId");
        }
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            conversationId = System.Guid.NewGuid().ToString();
        }

        var receiverRole = request?.RequesterRole;
        if (string.IsNullOrWhiteSpace(receiverRole))
        {
            receiverRole = Context.Get<string>("RequesterRole");
        }
        if (string.IsNullOrWhiteSpace(receiverRole))
        {
            receiverRole = request?.IsManufacturingRequest == true ? "DispatchingAgent" : "ProductAgent";
        }

        var senderId = string.IsNullOrWhiteSpace(Context.AgentId) ? "PlanningAgent" : Context.AgentId;
        var senderRole = string.IsNullOrWhiteSpace(Context.AgentRole) ? "PlanningAgent" : Context.AgentRole;
        
        // Build agent-specific response topic so the dispatcher (or product agent) receives it
        // Use the same response topic as proposals so refusals and proposals arrive on a common channel
        var topic = $"/{ns}/{resolvedReceiver}/OfferedCapability/Response";

        var refusal = new PlanningRefusalMessage(
            senderId ?? "PlanningAgent",
            senderRole ?? "PlanningAgent",
            resolvedReceiver,
            receiverRole ?? "DispatchingAgent",
            conversationId,
            refusalReason,
            failureDetail);

        await refusal.PublishAsync(client, topic).ConfigureAwait(false);
        
        // Mark conversation as refused to prevent duplicates
        Context.Set(alreadyRefusedKey, true);
        Context.Set("LastRefusalConversationId", conversationId);
        
        // Clear the current message so it won't be reprocessed
        Context.Set("LastReceivedMessage", (I40Sharp.Messaging.Models.I40Message?)null);
        Context.Set("CurrentMessage", (I40Sharp.Messaging.Models.I40Message?)null);
        
        Logger.LogInformation(
            "SendPlanningRefusal: published refusal to {Receiver} on {Topic} (conv={ConversationId}, reason={Reason})",
            resolvedReceiver,
            topic,
            conversationId,
            refusalReason);
        return NodeStatus.Success;
    }
}

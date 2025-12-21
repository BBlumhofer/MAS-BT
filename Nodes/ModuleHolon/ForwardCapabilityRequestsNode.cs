using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MAS_BT.Core;
using MAS_BT.Utilities;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.ModuleHolon;

/// <summary>
/// Robust forwarding node that listens for role-based broadcast CfPs and forwards them to internal planning agent topics.
/// Uses the new generic topic pattern: /{namespace}/ModuleHolon/broadcast/OfferedCapability/Request
/// </summary>
public class ForwardCapabilityRequestsNode : BTNode
{
    public string TargetTopicTemplate { get; set; } = "/{Namespace}/{ModuleId}/Planning/OfferedCapability/Request";
    public string ExpectedSenderRole { get; set; } = "Dispatching";

    private readonly ConcurrentQueue<I40Message> _pendingMessages = new();
    private readonly HashSet<string> _forwardedConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forwardedConvModule = new(StringComparer.OrdinalIgnoreCase);
    private bool _listenerRegistered;

    public ForwardCapabilityRequestsNode() : base("ForwardCapabilityRequests")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("ForwardCapabilityRequests: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        EnsureListener(client);

        if (!_pendingMessages.TryDequeue(out var message))
        {
            return NodeStatus.Failure; // No CfP waiting
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleId = Context.Get<string>("config.Agent.ModuleId") 
                       ?? Context.Get<string>("config.Agent.AgentId") 
                       ?? Context.AgentId 
                       ?? string.Empty;
        var agentRole = Context.AgentRole ?? "ModuleHolon";

        if (string.IsNullOrWhiteSpace(moduleId))
        {
            Logger.LogError("ForwardCapabilityRequests: ModuleId is null or empty! Cannot forward CfP.");
            return NodeStatus.Failure;
        }

        // Log receiver info before check
        Logger.LogInformation(
            "ForwardCapabilityRequests: checking targeting for moduleId={ModuleId}, agentRole={AgentRole}, receiver.id={ReceiverId}, receiver.role={ReceiverRole}",
            moduleId,
            agentRole,
            message.Frame?.Receiver?.Identification?.Id ?? "null",
            message.Frame?.Receiver?.Role?.Name ?? "null"
        );

        // Check if this message is targeted at this ModuleHolon
        if (!MessageTargetingHelper.IsTargetedAtAgent(message, moduleId, agentRole))
        {
            Logger.LogWarning(
                "ForwardCapabilityRequests: Message NOT targeted at {ModuleId} (receiver.id={ReceiverId}, receiver.role={ReceiverRole}), skipping",
                moduleId,
                message.Frame?.Receiver?.Identification?.Id ?? "null",
                message.Frame?.Receiver?.Role?.Name ?? "null"
            );
            return NodeStatus.Success; // Not an error, just not for us
        }

        var conv = message.Frame?.ConversationId ?? Guid.NewGuid().ToString();
        Context.Set("LastReceivedMessage", message);
        Context.Set("ForwardedConversationId", conv);

        // Store original requester (Dispatcher) for later reply
        var originalRequesterId = message.Frame?.Sender?.Identification?.Id;
        if (!string.IsNullOrWhiteSpace(originalRequesterId))
        {
            Context.Set($"OriginalRequester_{conv}", originalRequesterId);
        }

        // Check if already forwarded
        var convModuleKey = conv + ":" + moduleId;
        if (_forwardedConvModule.Contains(convModuleKey))
        {
            Logger.LogDebug("ForwardCapabilityRequests: already forwarded conversation {Conv} to module {Module}, skipping", conv, moduleId);
            return NodeStatus.Success;
        }

        // Build internal forward topic
        var targetTopic = TargetTopicTemplate
            .Replace("{Namespace}", ns)
            .Replace("{ModuleId}", moduleId);

        Logger.LogInformation(
            "ForwardCapabilityRequests: built targetTopic={Topic} from template={Template}, ns={Ns}, moduleId={ModuleId}",
            targetTopic,
            TargetTopicTemplate,
            ns,
            moduleId
        );

        // Determine message type (ManufacturingSequence vs ProcessChain)
        var incomingType = message.Frame?.Type ?? string.Empty;
        var isManufacturing = incomingType.IndexOf("ManufacturingSequence", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isManufacturing && targetTopic.IndexOf("ProcessChain", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            targetTopic = targetTopic.Replace("ProcessChain", "ManufacturingSequence");
        }

        // Forward to internal Planning sub-holon with updated receiver
        try
        {
            var planningAgentId = $"{moduleId}_Planning";
            var forwardedMessage = MessageTargetingHelper.CloneWithNewReceiver(message, planningAgentId, "PlanningHolon");

            // IMPORTANT: Keep original sender (Dispatcher) - ModuleHolon is just a forwarder
            // Planning needs to know the original requester to send the response back

            await client.PublishAsync(forwardedMessage, targetTopic).ConfigureAwait(false);
            _forwardedConvModule.Add(convModuleKey);

            Logger.LogInformation(
                "ForwardCapabilityRequests: forwarded CfP from {Sender} to internal Planning at {Topic} (conv={Conv})",
                message.Frame?.Sender?.Identification?.Id,
                targetTopic,
                conv
            );
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ForwardCapabilityRequests: failed to forward conversation {Conv} to {Module}", conv, moduleId);
            return NodeStatus.Failure;
        }

        if (!string.IsNullOrWhiteSpace(conv))
        {
            _forwardedConversations.Add(conv);
        }

        return NodeStatus.Success;
    }

    private void EnsureListener(MessagingClient client)
    {
        if (_listenerRegistered)
        {
            return;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        
        // Register listener for role-based broadcast CfPs
        client.OnMessage(message =>
        {
            try
            {
                if (message == null || !MatchesDispatcherOffer(message))
                {
                    return;
                }

                // Prevent forwarding messages sent by this agent (avoid self-forward loops)
                var senderId = message.Frame?.Sender?.Identification?.Id;
                if (!string.IsNullOrWhiteSpace(senderId) && string.Equals(senderId, Context.AgentId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Avoid duplicate forwards
                var conv = message.Frame?.ConversationId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(conv) && _forwardedConversations.Contains(conv))
                {
                    return;
                }

                _pendingMessages.Enqueue(message);
                Logger.LogInformation(
                    "ForwardCapabilityRequests: queued CfP conversation {Conv} from {Sender} (queue={Count})",
                    message.Frame?.ConversationId,
                    message.Frame?.Sender?.Identification?.Id,
                    _pendingMessages.Count
                );
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "ForwardCapabilityRequests: exception while queuing CfP");
            }
        });

        _listenerRegistered = true;
        Logger.LogInformation("ForwardCapabilityRequests: registered CfP listener for namespace {Namespace}", ns);
    }

    private bool MatchesDispatcherOffer(I40Message message)
    {
        if (message?.Frame == null)
        {
            return false;
        }

        var msgType = message.Frame.Type ?? string.Empty;

        // Accept generic CallForProposal
        if (string.Equals(msgType, I40MessageTypes.CALL_FOR_PROPOSAL, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Accept CallForProposal subtypes (ProcessChain, ManufacturingSequence, OfferedCapability, etc.)
        var prefix = I40MessageTypes.CALL_FOR_PROPOSAL + "/";
        if (msgType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

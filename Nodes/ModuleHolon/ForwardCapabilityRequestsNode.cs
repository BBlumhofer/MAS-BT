using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.ModuleHolon;

/// <summary>
/// Robust forwarding node that listens for dispatcher CfPs and forwards them to the internal planning agent topic.
/// </summary>
public class ForwardCapabilityRequestsNode : BTNode
{
    public string TargetTopicTemplate { get; set; } = "/{Namespace}/{ModuleId}/PlanningAgent/OfferRequest";
    public string ExpectedSenderRole { get; set; } = "DispatchingAgent";

    private readonly ConcurrentQueue<I40Message> _pendingMessages = new();
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
            // signal to fallback that no message is ready so it can execute its backoff branch
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var identifiers = ModuleContextHelper.ResolveModuleIdentifiers(Context);
        var receiverId = message.Frame?.Receiver?.Identification?.Id;
        var conv = message.Frame?.ConversationId ?? Guid.NewGuid().ToString();
        var publishedTopics = new List<string>();

        // If the dispatcher explicitly targeted a single ModuleId, forward only to that module's internal topic.
        IEnumerable<string> targetModuleIds;
        if (!string.IsNullOrWhiteSpace(receiverId) && identifiers.Contains(receiverId))
        {
            targetModuleIds = new[] { receiverId };
        }
        else
        {
            targetModuleIds = identifiers;
        }

        Context.Set("LastReceivedMessage", message);
        Context.Set("ForwardedConversationId", conv);

        foreach (var moduleId in targetModuleIds)
        {
            var targetTopic = TargetTopicTemplate
                .Replace("{Namespace}", ns)
                .Replace("{ModuleId}", moduleId);

            await client.PublishAsync(message, targetTopic);
            publishedTopics.Add(targetTopic);
        }

        Logger.LogInformation("ForwardCapabilityRequests: forwarded conversation {Conv} to {Topics}", conv, string.Join(", ", publishedTopics));

        return NodeStatus.Success;
    }

    private void EnsureListener(MessagingClient client)
    {
        if (_listenerRegistered)
        {
            return;
        }

        foreach (var type in EnumerateCfPTypes())
        {
            client.OnMessageType(type, message =>
            {
                try
                {
                    if (!MatchesDispatcherOffer(message))
                    {
                        return;
                    }

                    _pendingMessages.Enqueue(message);
                    Logger.LogInformation("ForwardCapabilityRequests: queued CfP conversation {Conv} (queue={Count})",
                        message.Frame?.ConversationId, _pendingMessages.Count);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "ForwardCapabilityRequests: exception while queuing CfP");
                }
            });
        }

        _listenerRegistered = true;
        Logger.LogInformation("ForwardCapabilityRequests: registered dispatcher offer listener for CfP sub-types");
    }

    private bool MatchesDispatcherOffer(I40Message message)
    {
        if (message?.Frame == null)
        {
            return false;
        }

        var senderRole = message.Frame.Sender?.Role?.Name ?? string.Empty;
        var receiverRole = message.Frame.Receiver?.Role?.Name ?? string.Empty;
        var receiverId = message.Frame.Receiver?.Identification?.Id ?? string.Empty;
        var identifiers = ModuleContextHelper.ResolveModuleIdentifiers(Context);

        if (!string.IsNullOrWhiteSpace(ExpectedSenderRole) &&
            !string.Equals(senderRole, ExpectedSenderRole, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // If the dispatcher targeted a specific ModuleId (receiver identification), accept only
        // when it matches one of our known identifiers. Otherwise accept broadcast CfPs where the
        // receiver role is ModuleHolon.
        if (!string.IsNullOrWhiteSpace(receiverId))
        {
            if (IsBroadcast(receiverId))
            {
                return string.Equals(receiverRole, "ModuleHolon", StringComparison.OrdinalIgnoreCase);
            }

            return identifiers.Contains(receiverId);
        }

        if (!string.Equals(receiverRole, "ModuleHolon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsBroadcast(string receiverId)
    {
        return string.Equals(receiverId, "broadcast", StringComparison.OrdinalIgnoreCase) || receiverId == "*";
    }

    private static IEnumerable<string> EnumerateCfPTypes()
    {
        yield return I40MessageTypes.CALL_FOR_PROPOSAL;
        yield return $"{I40MessageTypes.CALL_FOR_PROPOSAL}/{I40MessageTypeSubtypes.ProcessChain.ToProtocolString()}";
        yield return $"{I40MessageTypes.CALL_FOR_PROPOSAL}/{I40MessageTypeSubtypes.ManufacturingSequence.ToProtocolString()}";
    }

}

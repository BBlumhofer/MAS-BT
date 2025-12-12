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
        var conv = message.Frame?.ConversationId ?? Guid.NewGuid().ToString();
        var publishedTopics = new List<string>();

        Context.Set("LastReceivedMessage", message);
        Context.Set("ForwardedConversationId", conv);

        foreach (var moduleId in identifiers)
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

        client.OnMessageType(I40Sharp.Messaging.Models.I40MessageTypes.CALL_FOR_PROPOSAL, message =>
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

        _listenerRegistered = true;
        Logger.LogInformation("ForwardCapabilityRequests: registered dispatcher offer listener");
    }

    private bool MatchesDispatcherOffer(I40Message message)
    {
        if (message?.Frame == null)
        {
            return false;
        }

        var senderRole = message.Frame.Sender?.Role?.Name ?? string.Empty;
        var receiverRole = message.Frame.Receiver?.Role?.Name ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(ExpectedSenderRole) &&
            !string.Equals(senderRole, ExpectedSenderRole, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Dispatcher CfPs sent to ModuleHolons declare this role
        if (!string.Equals(receiverRole, "ModuleHolon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

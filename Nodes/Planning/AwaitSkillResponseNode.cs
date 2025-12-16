using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// AwaitSkillResponse - subscribes to SkillResponse topic and pulls the next response for the current conversation.
/// Minimal implementation: single-queue listener, non-blocking poll per tick.
/// </summary>
public class AwaitSkillResponseNode : BTNode
{
    public string ModuleId { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 100;

    private readonly ConcurrentQueue<string> _queue = new();
    private bool _subscribed;

    public AwaitSkillResponseNode() : base("AwaitSkillResponse")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("AwaitSkillResponse: MessagingClient missing");
            return NodeStatus.Failure;
        }

        var conversationId = Context.Get<string>("ConversationId");
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            Logger.LogError("AwaitSkillResponse: ConversationId missing in context");
            return NodeStatus.Failure;
        }

        if (!_subscribed)
        {
            var topic = TopicHelper.BuildTopic(Context, "SkillResponse");
            try
            {
                await client.SubscribeAsync(topic);
                var transport = client.GetType().GetField("_transport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(client);
                if (transport != null)
                {
                    var evt = transport.GetType().GetEvent("MessageReceived");
                    if (evt != null)
                    {
                        EventHandler<MessageReceivedEventArgs> handler = (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Payload))
                            {
                                _queue.Enqueue(e.Payload);
                            }
                        };
                        evt.AddEventHandler(transport, handler);
                    }
                }
                _subscribed = true;
                Logger.LogInformation("AwaitSkillResponse: Subscribed to {Topic}", topic);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "AwaitSkillResponse: Failed to subscribe");
                return NodeStatus.Failure;
            }
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(10, TimeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            if (_queue.TryDequeue(out var payload))
            {
                Context.Set("LastSkillResponsePayload", payload);
                Logger.LogInformation("AwaitSkillResponse: Received response payload for conversation {ConversationId}", conversationId);
                return NodeStatus.Success;
            }
            await Task.Delay(5);
        }

        return NodeStatus.Running;
    }
}

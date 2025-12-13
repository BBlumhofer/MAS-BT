using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Subscribes to a specific MQTT topic.
///
/// Additionally (optional, default enabled): registers an idempotent message handler
/// that writes matching incoming messages into the blackboard as
/// - CurrentMessage
/// - LastReceivedMessage
///
/// This mirrors the "subscribe + handler" pattern used by SubscribeAgentTopicsNode
/// so that downstream nodes can consistently consume messages from the blackboard.
/// </summary>
public class SubscribeToTopicNode : BTNode
{
    public string Topic { get; set; } = string.Empty;

    // Optional filters for the registered handler.
    // If not set, the node will try to infer a message type from the topic tail.
    public string? ExpectedType { get; set; } = null;
    public string? ExpectedTypes { get; set; } = null;
    public string? ExpectedSender { get; set; } = null;
    public string? ExpectedReceiver { get; set; } = null;

    // Behavior toggles
    public bool RegisterHandler { get; set; } = true;
    public bool AutoInferTypeFromTopic { get; set; } = true;
    public bool WriteCurrentMessage { get; set; } = true;
    public bool WriteLastReceivedMessage { get; set; } = true;
    
    public SubscribeToTopicNode() : base("SubscribeToTopic")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("SubscribeToTopic: MessagingClient not available or disconnected");
                return NodeStatus.Failure;
            }

            var topic = ResolvePlaceholders(Topic);
            if (string.IsNullOrWhiteSpace(topic))
            {
                Logger.LogError("SubscribeToTopic: Topic is empty after resolving placeholders");
                return NodeStatus.Failure;
            }

            await client.SubscribeAsync(topic);
            Logger.LogInformation("SubscribeToTopic: Successfully subscribed to {Topic}", topic);

            if (RegisterHandler)
            {
                RegisterMessageHandlerOnce(client, topic);
            }

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SubscribeToTopic: Error subscribing to topic");
            return NodeStatus.Failure;
        }
    }

    private void RegisterMessageHandlerOnce(MessagingClient client, string resolvedTopic)
    {
        // Prevent registering multiple callbacks if the BT re-ticks this node.
        var regKey = $"SubscribeToTopic.HandlerRegistered:{resolvedTopic}";
        if (Context.Get<bool>(regKey))
        {
            return;
        }

        var expectedReceiver = ResolveExpectedReceiverId();
        var expectedSender = string.IsNullOrWhiteSpace(ExpectedSender) ? null : ResolvePlaceholders(ExpectedSender);

        var expectedTypes = ParseExpectedTypes(resolvedTopic);

        void Handle(I40Message msg)
        {
            try
            {
                if (msg?.Frame == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(expectedSender)
                    && !string.Equals(msg.Frame.Sender?.Identification?.Id, expectedSender, StringComparison.Ordinal))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(expectedReceiver)
                    && !string.Equals(msg.Frame.Receiver?.Identification?.Id, expectedReceiver, StringComparison.Ordinal))
                {
                    return;
                }

                // If we use a global callback, apply type filtering here.
                if (expectedTypes.Count > 0
                    && !expectedTypes.Contains(msg.Frame.Type ?? string.Empty, StringComparer.Ordinal))
                {
                    return;
                }

                if (WriteLastReceivedMessage)
                {
                    Context.Set("LastReceivedMessage", msg);
                }

                if (WriteCurrentMessage)
                {
                    Context.Set("CurrentMessage", msg);
                }

                // Provide an INFO breadcrumb so users can see that the handler actually fired.
                Logger.LogInformation(
                    "SubscribeToTopic: Captured message (Type={Type}, Conv={Conv}, Sender={Sender})",
                    msg.Frame.Type,
                    msg.Frame.ConversationId,
                    msg.Frame.Sender?.Identification?.Id);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "SubscribeToTopic: Exception in message handler");
            }
        }

        if (expectedTypes.Count > 0)
        {
            foreach (var t in expectedTypes)
            {
                client.OnMessageType(t, Handle);
            }
        }
        else
        {
            // Fallback: subscribe globally and filter by sender/receiver only.
            client.OnMessage(Handle);
        }

        Context.Set(regKey, true);
        Logger.LogInformation(
            "SubscribeToTopic: Registered handler (Types={Types}, Receiver={Receiver})",
            expectedTypes.Count > 0 ? string.Join(",", expectedTypes) : "any",
            expectedReceiver ?? "any");
    }

    private string? ResolveExpectedReceiverId()
    {
        if (!string.IsNullOrWhiteSpace(ExpectedReceiver))
        {
            var v = ResolvePlaceholders(ExpectedReceiver);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        // Sensible default: this agent.
        var agentId = Context.Get<string>("AgentId")
                     ?? Context.Get<string>("config.Agent.AgentId")
                     ?? Context.AgentId;

        return string.IsNullOrWhiteSpace(agentId) ? null : agentId;
    }

    private List<string> ParseExpectedTypes(string resolvedTopic)
    {
        var list = new List<string>();

        if (!string.IsNullOrWhiteSpace(ExpectedType))
        {
            list.Add(ResolvePlaceholders(ExpectedType).Trim());
        }

        if (!string.IsNullOrWhiteSpace(ExpectedTypes))
        {
            var resolved = ResolvePlaceholders(ExpectedTypes);
            list.AddRange(resolved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // Backwards compatible heuristic for existing BT XMLs (e.g. Topic ends with /CalcSimilarity)
        if (list.Count == 0 && AutoInferTypeFromTopic)
        {
            var inferred = InferTypeFromTopic(resolvedTopic);
            if (!string.IsNullOrWhiteSpace(inferred))
            {
                list.Add(inferred);
            }
        }

        // Important: CallbackRegistry uses case-sensitive match (message.Frame.Type == MessageType).
        // Preserve the inferred / provided casing.
        return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? InferTypeFromTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;

        var tail = topic.TrimEnd('/');
        var idx = tail.LastIndexOf('/');
        var last = idx >= 0 ? tail[(idx + 1)..] : tail;
        if (string.IsNullOrWhiteSpace(last)) return null;

        // Special-cases for existing conventions
        if (string.Equals(last, "CalcSimilarity", StringComparison.Ordinal))
        {
            return "calcSimilarity";
        }

        // Generic: PascalCase topic segment -> lowerCamel type
        if (last.Length == 1)
        {
            return char.ToLowerInvariant(last[0]).ToString();
        }

        return char.ToLowerInvariant(last[0]) + last.Substring(1);
    }
}

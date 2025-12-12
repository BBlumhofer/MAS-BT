using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

using MAS_BT.Core;
using MAS_BT.Services;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// WaitForMessage - Wartet auf eingehende I4.0 Message (generic)
/// </summary>
public class WaitForMessageNode : BTNode
{
    public string? ExpectedType { get; set; } = null;
    public string? ExpectedTypes { get; set; } = null;
    public string? ExpectedSender { get; set; } = null;
    public string? ExpectedTopic { get; set; } = null;
    public string? ExpectedTopics { get; set; } = null;
    public int TimeoutSeconds { get; set; } = 30;
    // Optional: wait for messages belonging to a specific conversation.
    // Only used when explicitly provided; otherwise type/sender filters apply.
    public string? ExpectedConversationId { get; set; } = null;
    
    private readonly ConcurrentQueue<I40Message> _messageQueue = new();
    private bool _subscribed = false;
    private DateTime _startTime;
    private string? _usedConversationId = null;
    private readonly string _instanceId = Guid.NewGuid().ToString();
    private I40Message? _readyMessage = null;
    private volatile bool _hasReadyMessage = false;
    
    public WaitForMessageNode() : base("WaitForMessage")
    {
    }
    
    public WaitForMessageNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        if (!_subscribed)
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null)
            {
                Logger.LogError("WaitForMessage: MessagingClient not found");
                return NodeStatus.Failure;
            }

            // Subscribe to explicit topics if provided
            var topicsToSubscribe = ParseExpectedTopics();
            foreach (var topic in topicsToSubscribe)
            {
                try
                {
                    var resolvedTopic = ResolvePlaceholders(topic);
                    if (!string.IsNullOrWhiteSpace(resolvedTopic))
                    {
                        await client.SubscribeAsync(resolvedTopic);
                        Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Subscribed to topic {Topic}", Name, _instanceId, resolvedTopic);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "WaitForMessage[{Name}][{Inst}]: Failed to subscribe to topic {Topic}", Name, _instanceId, topic);
                }
            }
            var convToUse = ResolveConversationIdFromInput();
            if (!string.IsNullOrWhiteSpace(convToUse))
            {
                _usedConversationId = convToUse;
                client.OnConversation(convToUse, msg =>
                {
                        try
                        {
                            Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Conversation callback invoked (Type={Type}, Conv={Conv})", Name, _instanceId, msg.Frame.Type, msg.Frame.ConversationId);
                            _messageQueue.Enqueue(msg);
                            Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Enqueued message (Type={Type}, Conv={Conv}) - QueueCount={Count}", Name, _instanceId, msg.Frame.Type, msg.Frame.ConversationId, _messageQueue.Count);
                            _readyMessage = msg;
                            _hasReadyMessage = true;
                        }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "WaitForMessage[{Name}]: Exception in conversation callback", Name);
                    }
                });

                Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Subscribed to conversation {Conv}", Name, _instanceId, convToUse);
            }
            else
            {
                var expectedTypes = ParseExpectedTypes();

                if (expectedTypes.Count > 0)
                {
                    // Register per-type callbacks to let the registry do efficient filtering
                    foreach (var t in expectedTypes)
                    {
                        var typeTrim = t.Trim();
                        client.OnMessageType(typeTrim, msg =>
                        {
                                try
                                {
                                    Logger.LogDebug("WaitForMessage[{Name}][{Inst}]: MessageType callback invoked (Type={Type}, Conv={Conv})", Name, _instanceId, msg.Frame.Type, msg.Frame.ConversationId);
                                if (!string.IsNullOrEmpty(ExpectedSender) && msg.Frame.Sender.Identification.Id != ExpectedSender)
                                {
                                    Logger.LogDebug("WaitForMessage[{Name}][{Inst}]: Sender mismatch - expected {ExpectedSender}, got {Sender}", Name, _instanceId, ExpectedSender, msg.Frame.Sender.Identification.Id);
                                    return;
                                }
                                _messageQueue.Enqueue(msg);
                                Logger.LogDebug("WaitForMessage[{Name}][{Inst}]: Enqueued message (Type={Type}, Conv={Conv}) - QueueCount={Count}", Name, _instanceId, msg.Frame.Type, msg.Frame.ConversationId, _messageQueue.Count);
                                _readyMessage = msg;
                                _hasReadyMessage = true;
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError(ex, "WaitForMessage[{Name}][{Inst}]: Exception in message-type callback", Name, _instanceId);
                            }
                        });
                        Logger.LogDebug("WaitForMessage[{Name}][{Inst}]: Subscribed to message type {Type}", Name, _instanceId, typeTrim);
                    }
                }
                else
                {
                    // Fallback: register global message callback
                    client.OnMessage(msg =>
                    {
                        try
                        {
                            Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Global callback invoked (Type={Type}, Conv={Conv})", Name, _instanceId, msg.Frame.Type, msg.Frame.ConversationId);
                            if (!string.IsNullOrEmpty(ExpectedSender) && msg.Frame.Sender.Identification.Id != ExpectedSender)
                            {
                                Logger.LogDebug("WaitForMessage[{Name}][{Inst}]: Sender mismatch - expected {ExpectedSender}, got {Sender}", Name, _instanceId, ExpectedSender, msg.Frame.Sender.Identification.Id);
                                return;
                            }

                            // Additional type filtering if configured
                            var types = ParseExpectedTypes();
                            if (types.Count > 0 && !types.Contains(msg.Frame.Type, StringComparer.OrdinalIgnoreCase))
                            {
                                Logger.LogInformation("WaitForMessage[{Name}]: Type {Type} not in expected list", Name, msg.Frame.Type);
                                return;
                            }

                            _messageQueue.Enqueue(msg);
                            Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Enqueued message (Type={Type}, Conv={Conv}) - QueueCount={Count}", Name, _instanceId, msg.Frame.Type, msg.Frame.ConversationId, _messageQueue.Count);
                            _readyMessage = msg;
                            _hasReadyMessage = true;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "WaitForMessage[{Name}][{Inst}]: Exception in global callback", Name, _instanceId);
                        }
                    });

                    Logger.LogInformation("WaitForMessage[{Name}][{Inst}]: Waiting for message (Type: {Type}, Sender: {Sender})", Name, _instanceId, ExpectedTypes ?? "any", ExpectedSender ?? "any");
                }
            }

            _subscribed = true;
            _startTime = DateTime.UtcNow;
        }
        
        // Check timeout
        if ((DateTime.UtcNow - _startTime).TotalSeconds > TimeoutSeconds)
        {
            Logger.LogWarning("WaitForMessage: Timeout after {Timeout} seconds", TimeoutSeconds);
            // Reset timer so a subsequent tick can retry without being stuck in immediate timeouts
            _startTime = DateTime.UtcNow;
            return NodeStatus.Failure;
        }
        
        Logger.LogDebug("WaitForMessage[{Name}][{Inst}]: Checking queue before dequeue - QueueCount={Count} ReadyFlag={Ready}", Name, _instanceId, _messageQueue.Count, _hasReadyMessage);

        if (_hasReadyMessage && _readyMessage != null)
        {
            var ready = _readyMessage;
            _readyMessage = null;
            _hasReadyMessage = false;
            return HandleDequeuedMessage(ready);
        }

        if (_messageQueue.TryDequeue(out var queuedMessage))
        {
            return HandleDequeuedMessage(queuedMessage);
        }
        
        return NodeStatus.Running;
    }
    
    public override Task OnAbort()
    {
        _messageQueue.Clear();
        _subscribed = false;
        _usedConversationId = null;
        _readyMessage = null;
        _hasReadyMessage = false;
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        _messageQueue.Clear();
        _subscribed = false;
        _usedConversationId = null;
        _readyMessage = null;
        _hasReadyMessage = false;
        return Task.CompletedTask;
    }

    private List<string> ParseExpectedTypes()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(ExpectedType))
            list.Add(ExpectedType);
        if (!string.IsNullOrWhiteSpace(ExpectedTypes))
        {
            list.AddRange(ExpectedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return list;
    }

    private List<string> ParseExpectedTopics()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(ExpectedTopic))
            list.Add(ExpectedTopic);
        if (!string.IsNullOrWhiteSpace(ExpectedTopics))
        {
            list.AddRange(ExpectedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return list;
    }

    private NodeStatus HandleDequeuedMessage(I40Message message)
    {
        Context.Set("LastReceivedMessage", message);
        var receivedConv = message.Frame.ConversationId ?? string.Empty;
        var expectedConv = _usedConversationId ?? string.Empty;
        if (!string.IsNullOrEmpty(expectedConv))
        {
            if (string.Equals(expectedConv, receivedConv, StringComparison.Ordinal))
            {
                Logger.LogInformation("WaitForMessage: Received matching message for conversation '{Conv}' from '{Sender}'", receivedConv, message.Frame.Sender.Identification.Id);
            }
            else
            {
                Logger.LogWarning("WaitForMessage: Received message for conversation '{ReceivedConv}' but expected '{ExpectedConv}' — ignoring", receivedConv, expectedConv);
            }
        }
        else
        {
            Logger.LogInformation("WaitForMessage: Received message from '{Sender}' (no expected conversation)", message.Frame.Sender.Identification.Id);
        }
        var msgType = message.Frame.Type ?? string.Empty;
        if (string.Equals(msgType, "proposal", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("WaitForMessage: Proposal received for conversation '{Conv}'", receivedConv);
        }
        else if (string.Equals(msgType, "refuseProposal", StringComparison.OrdinalIgnoreCase) || string.Equals(msgType, "refusal", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("WaitForMessage: Refusal received for conversation '{Conv}'", receivedConv);
        }
        _startTime = DateTime.UtcNow;
        return NodeStatus.Success;
    }

    private string? ResolveConversationIdFromInput()
    {
        if (string.IsNullOrWhiteSpace(ExpectedConversationId))
        {
            return null;
        }

        var convToUse = ExpectedConversationId.Trim();

        try
        {
            if (convToUse.Contains('{') && convToUse.Contains('}'))
            {
                var start = convToUse.IndexOf('{');
                var end = convToUse.IndexOf('}', start + 1);
                if (start >= 0 && end > start)
                {
                    var token = convToUse.Substring(start + 1, end - start - 1);
                    try
                    {
                        object? tokenValue = null;
                        if (Context.Has(token))
                        {
                            tokenValue = Context.Get<object>(token);
                        }
                        else
                        {
                            tokenValue = Context.Get<string>(token);
                        }

                        var resolved = tokenValue?.ToString();
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            convToUse = resolved;
                        }
                    }
                    catch
                    {
                        // Ignore resolution issues and leave convToUse untouched
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "WaitForMessage: Placeholder resolution failed for '{ConvRaw}'", convToUse);
        }

        return string.IsNullOrWhiteSpace(convToUse) || convToUse.Contains('{') ? null : convToUse;
    }
}

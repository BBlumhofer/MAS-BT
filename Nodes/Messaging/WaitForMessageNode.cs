using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Services;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using System.Collections.Concurrent;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// WaitForMessage - Wartet auf eingehende I4.0 Message (generic)
/// </summary>
public class WaitForMessageNode : BTNode
{
    public string? ExpectedType { get; set; } = null;
    public string? ExpectedTypes { get; set; } = null;
    public string? ExpectedSender { get; set; } = null;
    public int TimeoutSeconds { get; set; } = 30;
    // Optional: wait for messages belonging to a specific conversation.
    // If null, behavior falls back to matching by Type/Sender as before.
    public string? ExpectedConversationId { get; set; } = null;
    
    private readonly ConcurrentQueue<I40Message> _messageQueue = new();
    private bool _subscribed = false;
    private DateTime _startTime;
    private string? _usedConversationId = null;
    
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
            // If an ExpectedConversationId is provided (or available in context), register a conversation callback
            var convToUse = ExpectedConversationId ?? Context.Get<string>("ConversationId");
            if (string.IsNullOrWhiteSpace(convToUse))
            {
                var currentRequest = Context.Get<SkillRequestEnvelope>("CurrentSkillRequest");
                convToUse = currentRequest?.ConversationId;
            }
            if (!string.IsNullOrWhiteSpace(convToUse))
            {
                // Resolve simple placeholder patterns like {ConversationId} using Context
                try
                {
                    if (convToUse.Contains('{') && convToUse.Contains('}'))
                    {
                        var start = convToUse.IndexOf('{');
                        var end = convToUse.IndexOf('}', start + 1);
                        if (start >= 0 && end > start)
                        {
                            var token = convToUse.Substring(start + 1, end - start - 1);
                            // Try various ways to resolve the token from Context (string or object)
                            try
                            {
                                if (Context.Has(token))
                                {
                                    var obj = Context.Get<object>(token);
                                    if (obj != null)
                                    {
                                        var s = obj.ToString();
                                        if (!string.IsNullOrWhiteSpace(s))
                                            convToUse = s;
                                    }
                                }
                                else
                                {
                                    // Fallback to string-get (older code paths)
                                    var resolved = Context.Get<string>(token);
                                    if (!string.IsNullOrWhiteSpace(resolved))
                                        convToUse = resolved;
                                }
                            }
                            catch
                            {
                                // ignore resolution failures and keep original convToUse
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "WaitForMessage: Placeholder resolution failed for '{ConvRaw}'", convToUse);
                }

                _usedConversationId = convToUse;
                client.OnConversation(convToUse, msg => _messageQueue.Enqueue(msg));
                Logger.LogInformation("WaitForMessage: Subscribed to conversation {Conv}", convToUse);
            }
            else
            {
                client.OnMessage(msg =>
                {
                    bool matches = true;

                    var expectedTypes = ParseExpectedTypes();
                    if (expectedTypes.Count > 0 && !expectedTypes.Contains(msg.Frame.Type, StringComparer.OrdinalIgnoreCase))
                        matches = false;

                    if (!string.IsNullOrEmpty(ExpectedSender) && 
                        msg.Frame.Sender.Identification.Id != ExpectedSender)
                        matches = false;

                    if (matches)
                    {
                        _messageQueue.Enqueue(msg);
                    }
                });

                Logger.LogInformation("WaitForMessage: Waiting for message (Type: {Type}, Sender: {Sender})", 
                    ExpectedType ?? "any", ExpectedSender ?? "any");
            }

            _subscribed = true;
            _startTime = DateTime.UtcNow;
        }
        
        // Check timeout
        if ((DateTime.UtcNow - _startTime).TotalSeconds > TimeoutSeconds)
        {
            Logger.LogWarning("WaitForMessage: Timeout after {Timeout} seconds", TimeoutSeconds);
            return NodeStatus.Failure;
        }
        
        if (_messageQueue.TryDequeue(out var message))
        {
            Context.Set("LastReceivedMessage", message);
            var receivedConv = message.Frame.ConversationId ?? string.Empty;
            var expectedConv = _usedConversationId ?? ExpectedConversationId ?? Context.Get<string>("ConversationId") ?? string.Empty;
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
                // Additional semantic logs for negotiation responses
                var msgType = message.Frame.Type ?? string.Empty;
                if (string.Equals(msgType, "proposal", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("WaitForMessage: Proposal received for conversation '{Conv}'", receivedConv);
                }
                else if (string.Equals(msgType, "refuseProposal", StringComparison.OrdinalIgnoreCase) || string.Equals(msgType, "refusal", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("WaitForMessage: Refusal received for conversation '{Conv}'", receivedConv);
                }
            return NodeStatus.Success;
        }
        
        return NodeStatus.Running;
    }
    
    public override Task OnAbort()
    {
        _messageQueue.Clear();
        _subscribed = false;
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        _messageQueue.Clear();
        _subscribed = false;
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
}

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
                _usedConversationId = convToUse;
                client.OnConversation(convToUse, msg => _messageQueue.Enqueue(msg));
                Logger.LogInformation("WaitForMessage: Subscribed to conversation {Conv}", convToUse);
            }
            else
            {
                client.OnMessage(msg =>
                {
                    bool matches = true;

                    if (!string.IsNullOrEmpty(ExpectedType) && msg.Frame.Type != ExpectedType)
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
            Logger.LogInformation("WaitForMessage: Received message from '{Sender}'", 
                message.Frame.Sender.Identification.Id);
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
}

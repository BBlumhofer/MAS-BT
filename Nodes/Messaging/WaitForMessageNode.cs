using Microsoft.Extensions.Logging;
using MAS_BT.Core;
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
    
    private readonly ConcurrentQueue<I40Message> _messageQueue = new();
    private bool _subscribed = false;
    private DateTime _startTime;
    
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
            
            _subscribed = true;
            _startTime = DateTime.UtcNow;
            Logger.LogInformation("WaitForMessage: Waiting for message (Type: {Type}, Sender: {Sender})", 
                ExpectedType ?? "any", ExpectedSender ?? "any");
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

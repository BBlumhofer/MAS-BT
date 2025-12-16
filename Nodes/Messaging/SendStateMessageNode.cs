using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models.Messages;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// SendStateMessage - Sendet Modulzustände via MQTT
/// Topic: /Modules/{ModuleID}/State/
/// </summary>
public class SendStateMessageNode : BTNode
{
    public string ModuleId { get; set; } = "";
    public bool IncludeModuleLocked { get; set; } = true;
    public bool IncludeModuleReady { get; set; } = true;
    
    public SendStateMessageNode() : base("SendStateMessage")
    {
    }
    
    public SendStateMessageNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("SendStateMessage: Publishing state for module '{ModuleId}'", ModuleId);
        
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("SendStateMessage: MessagingClient not found in context");
            return NodeStatus.Failure;
        }
        
        try
        {
            bool? isLocked = IncludeModuleLocked ? Context.Get<bool>($"module_{ModuleId}_locked") : null;
            bool? isReady = IncludeModuleReady ? Context.Get<bool>($"module_{ModuleId}_ready") : null;
            var hasError = Context.Get<bool>($"module_{ModuleId}_has_error");
            var moduleState = Context.Get<string>($"ModuleState_{ModuleId}")
                ?? Context.Get<string>("ModuleState")
                ?? (hasError ? "Error" : "Unknown");
            if (string.IsNullOrWhiteSpace(moduleState))
            {
                moduleState = hasError ? "Error" : "Unknown";
            }
            var startupSkillRunning = Context.Get<bool>("startupSkillRunning");

            var stateMessage = new StateMessage(
                isLocked,
                isReady,
                moduleState,
                hasError,
                startupSkillRunning);
            
            var message = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType("inform")
                .AddElement(stateMessage)
                .Build();
            
            var topic = TopicHelper.BuildTopic(Context, "State");
            await client.PublishAsync(message, topic);
            
            Logger.LogInformation("SendStateMessage: Published module state to topic '{Topic}'", topic);
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendStateMessage: Failed to send state message");
            return NodeStatus.Failure;
        }
    }
    
    public override Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        return Task.CompletedTask;
    }
}

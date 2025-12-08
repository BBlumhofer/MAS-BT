using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

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
            var stateCollection = new SubmodelElementCollection("ModuleState");
            
            if (IncludeModuleLocked)
            {
                var isLocked = Context.Get<bool>($"module_{ModuleId}_locked");
                var prop = new Property<bool>("ModuleLocked");
                prop.Value = new PropertyValue<bool>(isLocked);
                stateCollection.Add(prop);
            }
            
            if (IncludeModuleReady)
            {
                var isReady = Context.Get<bool>($"module_{ModuleId}_ready");
                var prop = new Property<bool>("ModuleReady");
                prop.Value = new PropertyValue<bool>(isReady);
                stateCollection.Add(prop);
            }
            
            // Füge weitere State-Informationen hinzu
            var hasError = Context.Get<bool>($"module_{ModuleId}_has_error");
            var errorProp = new Property<bool>("HasError");
            errorProp.Value = new PropertyValue<bool>(hasError);
            stateCollection.Add(errorProp);
            
            var message = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType("inform")
                .AddElement(stateCollection)
                .Build();
            
            var topic = $"/Modules/{ModuleId}/State/";
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

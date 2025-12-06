using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// UpdateInventoryFromAction - Aktualisiert Inventar nach Action-Completion
/// Liest aus Action.Effects oder Action.FinalResultData
/// </summary>
public class UpdateInventoryFromActionNode : BTNode
{
    public string ModuleId { get; set; } = "";
    public bool PublishToMqtt { get; set; } = false;
    
    public UpdateInventoryFromActionNode() : base("UpdateInventoryFromAction")
    {
    }
    
    public UpdateInventoryFromActionNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("UpdateInventoryFromAction: Updating inventory for module '{ModuleId}'", ModuleId);
        
        // Hole FinalResultData direkt aus Context (gesetzt von MonitoringSkill)
        var actionTitle = Context.Get<string>("ActionTitle");
        if (string.IsNullOrEmpty(actionTitle))
        {
            Logger.LogWarning("UpdateInventoryFromAction: No ActionTitle in context");
            return NodeStatus.Success; // Not an error, just skip
        }
        
        var finalResultData = Context.Get<IDictionary<string, object>>($"Skill_{actionTitle}_FinalResultData");
        if (finalResultData == null || !finalResultData.Any())
        {
            Logger.LogDebug("UpdateInventoryFromAction: No FinalResultData found for skill '{SkillName}'", actionTitle);
            return NodeStatus.Success; // Not an error, just no updates
        }
        
        try
        {
            // Speichere als ModuleInventory
            Context.Set("ModuleInventory", finalResultData);
            Logger.LogInformation("UpdateInventoryFromAction: Updated inventory with {Count} items", 
                finalResultData.Count);
            
            // Optional: Publish InventoryMessage via MQTT
            if (PublishToMqtt)
            {
                await PublishInventoryMessage(finalResultData);
            }
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UpdateInventoryFromAction: Failed to update inventory");
            return NodeStatus.Failure;
        }
    }
    
    private async Task PublishInventoryMessage(IDictionary<string, object> inventory)
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null) return;
        
        try
        {
            var inventoryCollection = new SubmodelElementCollection("Inventory");
            foreach (var kvp in inventory)
            {
                var prop = I40MessageBuilder.CreateStringProperty(kvp.Key, kvp.Value?.ToString() ?? "");
                inventoryCollection.Add(prop);
            }
            
            var message = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType(I40MessageTypes.INFORM)
                .AddElement(inventoryCollection)
                .Build();
            
            var topic = $"/Modules/{ModuleId}/Inventory/";
            await client.PublishAsync(message, topic);
            
            Logger.LogInformation("UpdateInventoryFromAction: Published inventory to MQTT");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "UpdateInventoryFromAction: Failed to publish inventory");
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

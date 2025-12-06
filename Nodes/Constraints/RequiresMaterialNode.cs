// filepath: /home/benjamin/AgentDevelopment/MAS-BT/Nodes/Constraints/RequiresMaterialNode.cs
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// RequiresMaterial - Prüft ob genug Material für den Prozessschritt verfügbar ist
/// Verhindert Skill-Ausführung bei unzureichendem Pufferstand
/// </summary>
public class RequiresMaterialNode : BTNode
{
    /// <summary>
    /// ID des benötigten Materials/Items
    /// </summary>
    public string ItemId { get; set; } = "";
    
    /// <summary>
    /// Mindestmenge des Materials
    /// </summary>
    public int Quantity { get; set; } = 1;
    
    /// <summary>
    /// Modul in dem das Material geprüft wird
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    public RequiresMaterialNode() : base("RequiresMaterial")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        
        Logger.LogDebug("RequiresMaterial: Checking '{ItemId}' x{Quantity} in module '{ModuleId}'", 
            ItemId, Quantity, moduleId);
        
        try
        {
            if (string.IsNullOrEmpty(ItemId))
            {
                Logger.LogWarning("RequiresMaterial: No ItemId specified");
                return NodeStatus.Failure;
            }
            
            var (available, actualCount) = await CheckMaterialAvailability(moduleId, ItemId, Quantity);
            
            Context.Set($"material_{ItemId}_available", available);
            Context.Set($"material_{ItemId}_count", actualCount);
            
            if (available)
            {
                Logger.LogInformation("RequiresMaterial: Material '{ItemId}' available ({ActualCount}/{Required})", 
                    ItemId, actualCount, Quantity);
                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning("RequiresMaterial: Insufficient material '{ItemId}' ({ActualCount}/{Required})", 
                    ItemId, actualCount, Quantity);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RequiresMaterial: Error checking material availability");
            return NodeStatus.Failure;
        }
    }
    
    private Task<(bool Available, int ActualCount)> CheckMaterialAvailability(string moduleId, string itemId, int requiredQuantity)
    {
        int foundCount = 0;
        
        // Methode 1: Aus Inventory im Context (InventoryMessage Format)
        if (Context.Has($"Inventory_{moduleId}"))
        {
            var inventory = Context.Get<List<Dictionary<string, object>>>($"Inventory_{moduleId}");
            if (inventory != null)
            {
                foreach (var storage in inventory)
                {
                    if (storage.TryGetValue("slots", out var slotsObj) && slotsObj is List<object> slots)
                    {
                        foreach (var slotObj in slots)
                        {
                            if (slotObj is Dictionary<string, object> slot && 
                                slot.TryGetValue("content", out var contentObj) &&
                                contentObj is Dictionary<string, object> content)
                            {
                                // Prüfe ProductType, ProductID, CarrierType oder CarrierID
                                if (MatchesItem(content, itemId))
                                {
                                    foundCount++;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        // Methode 2: Aus MaterialStock im Context
        if (Context.Has($"MaterialStock_{moduleId}"))
        {
            var stock = Context.Get<Dictionary<string, int>>($"MaterialStock_{moduleId}");
            if (stock != null && stock.TryGetValue(itemId, out var count))
            {
                foundCount = count;
            }
        }
        
        return Task.FromResult((foundCount >= requiredQuantity, foundCount));
    }
    
    private bool MatchesItem(Dictionary<string, object> content, string itemId)
    {
        // Prüfe verschiedene Felder
        var fieldsToCheck = new[] { "ProductType", "ProductID", "CarrierType", "CarrierID", "ItemId" };
        
        foreach (var field in fieldsToCheck)
        {
            if (content.TryGetValue(field, out var value) && 
                value?.ToString()?.Equals(itemId, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }
        
        return false;
    }
}

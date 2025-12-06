// filepath: /home/benjamin/AgentDevelopment/MAS-BT/Nodes/Constraints/ProductMatchesOrderNode.cs
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// ProductMatchesOrder - Vergleicht Modul-Inventar/Produkt-Identifier mit erwartetem Produkttyp/-ID
/// Stellt sicher, dass das richtige Produkt verarbeitet wird
/// </summary>
public class ProductMatchesOrderNode : BTNode
{
    /// <summary>
    /// Erwartetes Produkt (ProductType oder ProductId)
    /// </summary>
    public string ExpectedProductType { get; set; } = "";
    
    /// <summary>
    /// Erwartete Produkt-ID (optional, für exakte Matches)
    /// </summary>
    public string ExpectedProductId { get; set; } = "";
    
    /// <summary>
    /// Modul in dem das Produkt geprüft wird
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    /// <summary>
    /// Slot-Index im Inventar (optional, -1 = alle Slots prüfen)
    /// </summary>
    public int SlotIndex { get; set; } = -1;

    public ProductMatchesOrderNode() : base("ProductMatchesOrder")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        
        Logger.LogDebug("ProductMatchesOrder: Checking product in module '{ModuleId}'", moduleId);
        
        try
        {
            if (string.IsNullOrEmpty(ExpectedProductType) && string.IsNullOrEmpty(ExpectedProductId))
            {
                Logger.LogWarning("ProductMatchesOrder: No ExpectedProductType or ExpectedProductId specified");
                return NodeStatus.Failure;
            }
            
            var (matches, actualProduct) = await CheckProductMatch(moduleId);
            
            Context.Set($"product_matches_order_{moduleId}", matches);
            Context.Set($"actual_product_{moduleId}", actualProduct);
            
            if (matches)
            {
                Logger.LogInformation("ProductMatchesOrder: Product '{ActualProduct}' matches expected in module '{ModuleId}'", 
                    actualProduct, moduleId);
                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning("ProductMatchesOrder: Product mismatch in module '{ModuleId}'. Expected: Type='{ExpectedType}' Id='{ExpectedId}', Actual: '{Actual}'", 
                    moduleId, ExpectedProductType, ExpectedProductId, actualProduct);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ProductMatchesOrder: Error checking product match");
            return NodeStatus.Failure;
        }
    }
    
    private Task<(bool matches, string actualProduct)> CheckProductMatch(string moduleId)
    {
        // Aus Inventory im Context prüfen
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
                            if (slotObj is Dictionary<string, object> slot)
                            {
                                // Prüfe Slot-Index wenn spezifiziert
                                if (SlotIndex >= 0 && 
                                    slot.TryGetValue("index", out var indexObj) && 
                                    Convert.ToInt32(indexObj) != SlotIndex)
                                {
                                    continue;
                                }
                                
                                if (slot.TryGetValue("content", out var contentObj) && 
                                    contentObj is Dictionary<string, object> content)
                                {
                                    var productType = content.TryGetValue("ProductType", out var pt) ? pt?.ToString() : "";
                                    var productId = content.TryGetValue("ProductID", out var pi) ? pi?.ToString() : "";
                                    var isEmpty = content.TryGetValue("IsSlotEmpty", out var empty) && 
                                                  (empty is bool b ? b : bool.Parse(empty?.ToString() ?? "true"));
                                    
                                    if (isEmpty) continue;
                                    
                                    // Prüfe Match
                                    bool typeMatches = string.IsNullOrEmpty(ExpectedProductType) || 
                                                       productType?.Equals(ExpectedProductType, StringComparison.OrdinalIgnoreCase) == true;
                                    bool idMatches = string.IsNullOrEmpty(ExpectedProductId) || 
                                                     productId?.Equals(ExpectedProductId, StringComparison.OrdinalIgnoreCase) == true;
                                    
                                    if (typeMatches && idMatches)
                                    {
                                        return Task.FromResult((true, $"{productType}/{productId}"));
                                    }
                                    
                                    // Bei Slot-spezifischer Prüfung: Wenn nicht passt, Fehler
                                    if (SlotIndex >= 0)
                                    {
                                        return Task.FromResult((false, $"{productType}/{productId}"));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        
        // Aus CurrentProduct im Context
        if (Context.Has($"CurrentProduct_{moduleId}"))
        {
            var currentProduct = Context.Get<Dictionary<string, string>>($"CurrentProduct_{moduleId}");
            if (currentProduct != null)
            {
                var productType = currentProduct.GetValueOrDefault("ProductType", "");
                var productId = currentProduct.GetValueOrDefault("ProductID", "");
                
                bool typeMatches = string.IsNullOrEmpty(ExpectedProductType) || 
                                   productType.Equals(ExpectedProductType, StringComparison.OrdinalIgnoreCase);
                bool idMatches = string.IsNullOrEmpty(ExpectedProductId) || 
                                 productId.Equals(ExpectedProductId, StringComparison.OrdinalIgnoreCase);
                
                return Task.FromResult((typeMatches && idMatches, $"{productType}/{productId}"));
            }
        }
        
        Logger.LogDebug("ProductMatchesOrder: No product information found for module '{ModuleId}'", moduleId);
        return Task.FromResult((false, "NoProduct"));
    }
}

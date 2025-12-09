using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models.Messages;
using UAClient.Client;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// UpdateInventory - published inventory snapshots (either current ModuleInventory or latest action result)
/// </summary>
public class UpdateInventoryNode : BTNode
{
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public bool PublishToMqtt { get; set; } = false;
    public bool ForcePublish { get; set; } = false;
    
    public UpdateInventoryNode() : base("UpdateInventory")
    {
    }
    
    public UpdateInventoryNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("UpdateInventory: start publish for module '{ModuleId}' (ForcePublish={ForcePublish})", ModuleId, ForcePublish);

        // Prefer structured storage units if available; fallback to legacy flat dictionary or remote read.
        var storageUnits = Context.Get<List<StorageUnit>>("ModuleInventory");
        if (storageUnits != null)
        {
            Logger.LogDebug("UpdateInventory: found ModuleInventory with {StorageCount} storage units", storageUnits.Count);
        }

        if ((storageUnits == null || storageUnits.Count == 0))
        {
            // Try to pull directly from RemoteServer storages
            storageUnits = ReadStoragesFromRemote();
            if (storageUnits != null && storageUnits.Count > 0)
            {
                Logger.LogInformation("UpdateInventory: pulled {StorageCount} storages from RemoteServer", storageUnits.Count);
                Context.Set("ModuleInventory", storageUnits);
            }
        }

        if ((storageUnits == null || storageUnits.Count == 0))
        {
            var legacyInventory = Context.Get<IDictionary<string, object?>>("ModuleInventory");
            var actionTitle = Context.Get<string>("ActionTitle");

            if ((legacyInventory == null || !legacyInventory.Any()) && !string.IsNullOrEmpty(actionTitle))
            {
                legacyInventory = Context.Get<IDictionary<string, object?>>("Skill_" + actionTitle + "_FinalResultData");
                if (legacyInventory != null)
                {
                    Logger.LogDebug("UpdateInventory: using FinalResultData from action '{ActionTitle}' with {ItemCount} entries", actionTitle, legacyInventory.Count);
                }
            }

            storageUnits = ConvertLegacyInventory(legacyInventory);
            if (storageUnits != null)
            {
                Logger.LogDebug("UpdateInventory: converted legacy inventory into {StorageCount} storage units", storageUnits.Count);
            }
        }

        if ((storageUnits == null || storageUnits.Count == 0) && !ForcePublish)
        {
            Logger.LogWarning("UpdateInventory: No inventory data available; skipping publish");
            return NodeStatus.Success;
        }
        
        try
        {
            if (PublishToMqtt)
            {
                await PublishInventoryMessage(storageUnits ?? new List<StorageUnit>());
            }

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UpdateInventory: Failed to publish inventory");
            return NodeStatus.Failure;
        }
    }

    private List<StorageUnit>? ConvertLegacyInventory(IDictionary<string, object?>? inventory)
    {
        if (inventory == null || !inventory.Any())
            return null;

        var storage = new StorageUnit
        {
            Name = "Inventory",
            Slots = inventory
                .Select((kvp, idx) => new Slot
                {
                    Index = idx,
                    Content = new SlotContent
                    {
                        ProductID = kvp.Key,
                        ProductType = kvp.Key,
                        CarrierID = kvp.Value?.ToString() ?? string.Empty,
                        CarrierType = string.Empty,
                        IsSlotEmpty = false
                    }
                })
                .ToList()
        };

        return new List<StorageUnit> { storage };
    }

    private List<StorageUnit>? ReadStoragesFromRemote()
    {
        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogWarning("UpdateInventory: RemoteServer not found in context");
                return null;
            }

            var moduleName = string.IsNullOrWhiteSpace(ModuleName) ? ModuleId : ModuleName;
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                Logger.LogWarning("UpdateInventory: ModuleName/ModuleId not provided for storage read");
                return null;
            }

            if (!server.Modules.TryGetValue(moduleName, out var module))
            {
                Logger.LogWarning("UpdateInventory: Module '{ModuleName}' not found on RemoteServer", moduleName);
                return null;
            }

            if (module.Storages == null || module.Storages.Count == 0)
            {
                Logger.LogWarning("UpdateInventory: Module '{ModuleName}' has no storages", moduleName);
                return new List<StorageUnit>();
            }

            var list = new List<StorageUnit>();
            foreach (var storageKv in module.Storages)
            {
                var storage = storageKv.Value;
                var slots = storage.Slots?.Values ?? Enumerable.Empty<RemoteStorageSlot>();
                var mappedSlots = new List<Slot>();

                var idx = 0;
                foreach (var slot in slots)
                {
                    mappedSlots.Add(new Slot
                    {
                        Index = idx++,
                        Content = new SlotContent
                        {
                            CarrierID = slot.CarrierId ?? string.Empty,
                            CarrierType = slot.CarrierTypeDisplay() ?? string.Empty,
                            ProductID = slot.ProductId ?? string.Empty,
                            ProductType = slot.ProductTypeDisplay() ?? string.Empty,
                            IsSlotEmpty = slot.IsSlotEmpty ?? true
                        }
                    });
                }

                list.Add(new StorageUnit
                {
                    Name = storage.Name ?? storageKv.Key,
                    Slots = mappedSlots
                });
            }

            return list;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "UpdateInventory: failed to read storages from RemoteServer");
            return null;
        }
    }
    
    private async Task PublishInventoryMessage(List<StorageUnit> storageUnits)
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null) return;
        
        try
        {
            var inventoryCollection = new InventoryMessage(storageUnits);
            Logger.LogInformation("UpdateInventory: building InventoryMessage with {StorageCount} storages and {SlotCount} slots", 
                storageUnits.Count, storageUnits.SelectMany(s => s.Slots ?? new List<Slot>()).Count());
            var message = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType(I40MessageTypes.INFORM)
                .AddElement(inventoryCollection)
                .Build();
            
            var topic = $"/Modules/{ModuleId}/Inventory/";
            await client.PublishAsync(message, topic);
            
            Logger.LogInformation("UpdateInventory: Published inventory to MQTT at {topic}", topic);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "UpdateInventory: Failed to publish inventory");
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

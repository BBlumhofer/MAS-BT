using System;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using AasSharpClient.Models.Messages;

namespace MAS_BT.Nodes.ModuleHolon;

public class ReadCachedSnapshotsNode : BTNode
{
    public ReadCachedSnapshotsNode() : base("ReadCachedSnapshots") { }

    public override Task<NodeStatus> Execute()
    {
        var moduleId = ModuleContextHelper.ResolveModuleId(Context);

        var inventoryCache = Context.Get<CachedInventoryData>($"InventoryCache_{moduleId}");
        var neighborsCache = Context.Get<CachedNeighborsData>($"NeighborsCache_{moduleId}");

        if (inventoryCache != null && inventoryCache.StorageUnits != null && inventoryCache.StorageUnits.Count > 0)
        {
            Context.Set("ModuleInventory", inventoryCache.StorageUnits);
            Logger.LogInformation("ReadCachedSnapshots: loaded {Count} storage units from cache for {Module}", inventoryCache.StorageUnits.Count, moduleId);
            try
            {
                var first = inventoryCache.StorageUnits[0];
                var slotCount = first.Slots?.Count ?? 0;
                var sample = slotCount > 0 ? first.Slots[0].Content : null;
                Logger.LogDebug("ReadCachedSnapshots: sample storage='{Storage}' slots={SlotCount} sampleContent={Sample}", first.Name, slotCount, sample);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "ReadCachedSnapshots: failed to log sample inventory");
            }
        }

        if (neighborsCache != null && neighborsCache.Neighbors != null)
        {
            Context.Set("Neighbors", neighborsCache.Neighbors);
            Logger.LogInformation("ReadCachedSnapshots: loaded {Count} neighbors from cache for {Module}", neighborsCache.Neighbors.Count, moduleId);
        }

        return Task.FromResult(NodeStatus.Success);
    }
}

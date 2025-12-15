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

        var storageUnits = inventoryCache?.StorageUnits;
        if (storageUnits != null && storageUnits.Count > 0)
        {
            Context.Set("ModuleInventory", storageUnits);
            Logger.LogInformation("ReadCachedSnapshots: loaded {Count} storage units from cache for {Module}", storageUnits.Count, moduleId);
            try
            {
                var first = storageUnits[0];
                var slotCount = first.Slots?.Count ?? 0;
                var sample = slotCount > 0 ? first.Slots[0].Content : null;
                Logger.LogDebug("ReadCachedSnapshots: sample storage='{Storage}' slots={SlotCount} sampleContent={Sample}", first.Name, slotCount, sample);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "ReadCachedSnapshots: failed to log sample inventory");
            }
        }

        var neighbors = neighborsCache?.Neighbors;
        if (neighbors != null)
        {
            Context.Set("Neighbors", neighbors);
            Logger.LogInformation("ReadCachedSnapshots: loaded {Count} neighbors from cache for {Module}", neighbors.Count, moduleId);
        }

        return Task.FromResult(NodeStatus.Success);
    }
}

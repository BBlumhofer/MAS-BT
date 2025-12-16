using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// Synchronisiert Inventory-Daten (StorageUnits) in Neo4j.
/// Erstellt/aktualisiert Storage-Nodes und HAS_STORAGE Relationships.
/// </summary>
public class SyncInventoryToNeo4jNode : BTNode
{
    public SyncInventoryToNeo4jNode() : base("SyncInventoryToNeo4j") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var driver = Context.Get<IDriver>("Neo4jDriver");
            if (driver == null)
            {
                Logger.LogWarning("SyncInventoryToNeo4j: Neo4jDriver not available in context");
                return NodeStatus.Failure;
            }

            var database = Context.Get<string>("config.Neo4j.Database")
                         ?? Environment.GetEnvironmentVariable("NEO4J_DATABASE")
                         ?? "neo4j";

            var message = Context.Get<I40Message>("LastReceivedMessage");
            if (message == null)
            {
                Logger.LogWarning("SyncInventoryToNeo4j: No LastReceivedMessage in context");
                return NodeStatus.Failure;
            }

            // Check if message contains InventoryMessage
            if (!InventoryMessage.ContainsStorageUnits(message.InteractionElements))
            {
                Logger.LogDebug("SyncInventoryToNeo4j: No StorageUnits in message, skipping");
                return NodeStatus.Success;
            }

            var inventory = new InventoryMessage(message.InteractionElements);
            var storageUnits = inventory.StorageUnits;

            if (storageUnits.Count == 0)
            {
                Logger.LogDebug("SyncInventoryToNeo4j: Empty StorageUnits list, skipping");
                return NodeStatus.Success;
            }

            // Determine module ID (sender or from context)
            var rawSenderId = message.Frame?.Sender?.Identification?.Id;
            var moduleId = NormalizeModuleId(rawSenderId)
                        ?? Context.Get<string>("config.Agent.ModuleId")
                        ?? Context.Get<string>("ModuleId")
                        ?? Context.Get<string>("config.Agent.AgentId")
                        ?? "UnknownModule";

            await using var session = driver.AsyncSession(o => o.WithDatabase(database));

            var timestamp = DateTime.UtcNow;
            var syncedCount = 0;

            foreach (var storage in storageUnits)
            {
                var storageName = storage.Name ?? "UnnamedStorage";
                var storageId = $"{moduleId}_{storageName}";

                var totalSlots = storage.Slots.Count;
                var freeSlots = storage.Slots.Count(s => s.Content.IsSlotEmpty);
                var occupiedSlots = totalSlots - freeSlots;

                                var slots = storage.Slots
                                        .Select(slot => new
                                        {
                                                slotId = $"{storageId}_Slot_{slot.Index}",
                                                index = slot.Index,
                                                carrierId = slot.Content.CarrierID ?? string.Empty,
                                                carrierType = slot.Content.CarrierType ?? string.Empty,
                                                productType = slot.Content.ProductType ?? string.Empty,
                                                productId = slot.Content.ProductID ?? string.Empty,
                                                isEmpty = slot.Content.IsSlotEmpty
                                        })
                                        .ToList();

                var query = @"
MERGE (agent:Agent {agentId: $moduleId})
MERGE (s:Storage {storageId: $storageId})
SET 
  s.name = $storageName,
  s.moduleId = $moduleId,
  s.totalSlots = $totalSlots,
  s.freeSlots = $freeSlots,
  s.occupiedSlots = $occupiedSlots,
  s.lastUpdated = datetime($timestamp)

MERGE (agent)-[:HAS_STORAGE]->(s)

WITH s, $slots AS slots, $timestamp AS ts
UNWIND slots AS slot
MERGE (sl:Slot {slotId: slot.slotId})
SET
    sl.index = slot.index,
    sl.carrierId = slot.carrierId,
    sl.carrierType = slot.carrierType,
    sl.productType = slot.productType,
    sl.productId = slot.productId,
    sl.isEmpty = slot.isEmpty,
    sl.lastUpdated = datetime(ts)

MERGE (s)-[hs:HAS_SLOT]->(sl)
SET hs.index = slot.index

RETURN s.storageId AS storageId, count(sl) AS slotCount
";

                var parameters = new
                {
                    moduleId = moduleId,
                    storageId = storageId,
                    storageName = storageName,
                    totalSlots = totalSlots,
                    freeSlots = freeSlots,
                    occupiedSlots = occupiedSlots,
                    slots = slots,
                    timestamp = timestamp.ToString("o")
                };

                var cursor = await session.RunAsync(query, parameters);
                var result = await cursor.SingleOrDefaultAsync();

                if (result != null)
                {
                    syncedCount++;
                    var slotCount = result["slotCount"].As<long>();
                    Logger.LogDebug(
                        "SyncInventoryToNeo4j: Synced storage {Storage} for module {Module} (slots={Slots}, free={Free}, occupied={Occupied})",
                        storageName,
                        moduleId,
                        slotCount,
                        freeSlots,
                        occupiedSlots);
                }
            }

            Logger.LogInformation(
                "SyncInventoryToNeo4j: Synced {Count} storage units for module {Module}",
                syncedCount,
                moduleId);

            return syncedCount > 0 ? NodeStatus.Success : NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SyncInventoryToNeo4j: Exception during Neo4j sync");
            return NodeStatus.Failure;
        }
    }

    private static string? NormalizeModuleId(string? senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId)) return null;

        // Common sub-holon naming conventions
        if (senderId.EndsWith("_Execution", StringComparison.OrdinalIgnoreCase))
        {
            return senderId.Substring(0, senderId.Length - "_Execution".Length);
        }

        if (senderId.EndsWith("_Planning", StringComparison.OrdinalIgnoreCase))
        {
            return senderId.Substring(0, senderId.Length - "_Planning".Length);
        }

        if (senderId.EndsWith("_PlanningAgent", StringComparison.OrdinalIgnoreCase))
        {
            return senderId.Substring(0, senderId.Length - "_PlanningAgent".Length);
        }

        if (senderId.EndsWith("_ExecutionAgent", StringComparison.OrdinalIgnoreCase))
        {
            return senderId.Substring(0, senderId.Length - "_ExecutionAgent".Length);
        }

        return senderId;
    }
}

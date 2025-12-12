using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using AasSharpClient.Models.Messages;

namespace MAS_BT.Nodes.ModuleHolon;

public class SubscribeModuleHolonTopicsNode : BTNode
{
    public SubscribeModuleHolonTopicsNode() : base("SubscribeModuleHolonTopics") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("SubscribeModuleHolonTopics: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var primaryModuleId = ModuleContextHelper.ResolveModuleId(Context);
        var moduleIdentifiers = ModuleContextHelper.ResolveModuleIdentifiers(Context);
        var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"/{ns}/DispatchingAgent/Offer"
        };

        foreach (var moduleId in moduleIdentifiers)
        {
            topics.Add($"/{ns}/{moduleId}/ScheduleAction");
            topics.Add($"/{ns}/{moduleId}/BookingConfirmation");
            topics.Add($"/{ns}/{moduleId}/TransportPlan");
            topics.Add($"/{ns}/{moduleId}/PlanningAgent/OfferResponse");
            topics.Add($"/{ns}/{moduleId}/register");
            topics.Add($"/{ns}/{moduleId}/Inventory");
            topics.Add($"/{ns}/{moduleId}/Neighbors");
        }

        var ok = 0;
        foreach (var topic in topics)
        {
            try
            {
                await client.SubscribeAsync(topic);
                ok++;
                Logger.LogInformation("SubscribeModuleHolonTopics: subscribed {Topic}", topic);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SubscribeModuleHolonTopics: failed to subscribe {Topic}", topic);
            }
        }

        // Attach a single message handler (idempotent) to cache inventory/neighbors when they arrive
        if (!Context.Get<bool>("ModuleHolonTopicsHandlerRegistered"))
        {
            client.OnMessage(m =>
            {
                if (IsInventoryUpdate(m))
                {
                    var cache = BuildInventoryCache(m);
                    if (cache != null)
                    {
                        Context.Set($"InventoryCache_{primaryModuleId}", cache);
                        // Keep a current, structured snapshot in context for immediate use
                        Context.Set("ModuleInventory", cache.StorageUnits);
                        Logger.LogInformation("SubscribeModuleHolonTopics: cached inventory for {ModuleId}", primaryModuleId);
                    }
                }
                else if (IsNeighborsUpdate(m))
                {
                    var cache = BuildNeighborsCache(m);
                    if (cache != null)
                    {
                        Context.Set($"NeighborsCache_{primaryModuleId}", cache);
                        // Also expose the parsed neighbors directly to the context
                        Context.Set("Neighbors", cache.Neighbors);
                        Logger.LogInformation("SubscribeModuleHolonTopics: cached neighbors for {ModuleId}", primaryModuleId);
                    }
                }
            });
            Context.Set("ModuleHolonTopicsHandlerRegistered", true);
        }

        return ok > 0 ? NodeStatus.Success : NodeStatus.Failure;
    }

    private static CachedInventoryData? BuildInventoryCache(I40Message? message)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        var inventory = new InventoryMessage(message.InteractionElements);
        var storageUnits = inventory.StorageUnits?.ToList() ?? new List<StorageUnit>();
        if (storageUnits.Count == 0)
        {
            return null;
        }

        return new CachedInventoryData(storageUnits, DateTime.UtcNow);
    }

    private static CachedNeighborsData? BuildNeighborsCache(I40Message? message)
    {
        var neighbors = ExtractNeighborNames(message);
        return new CachedNeighborsData(neighbors, DateTime.UtcNow);
    }

    private static bool IsInventoryUpdate(I40Message? message)
    {
        if (message == null)
        {
            return false;
        }

        var type = message.Frame?.Type;
        if (string.Equals(type, "inventoryUpdate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return InventoryMessage.ContainsStorageUnits(message.InteractionElements);
    }

    private static bool IsNeighborsUpdate(I40Message? message)
    {
        if (message == null)
        {
            return false;
        }

        var type = message.Frame?.Type;
        if (string.Equals(type, "neighborsUpdate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsNeighborCollection(message.InteractionElements);
    }

    private static bool ContainsNeighborCollection(IEnumerable<ISubmodelElement>? interactionElements)
    {
        if (interactionElements == null)
        {
            return false;
        }

        if (interactionElements.OfType<SubmodelElementCollection>()
            .Any(c => string.Equals(c.IdShort, "Neighbors", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return interactionElements.OfType<IProperty>()
            .Any(p => string.Equals(p.IdShort, "Neighbors", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ExtractNeighborNames(I40Message? message)
    {
        if (message?.InteractionElements == null)
        {
            return new List<string>();
        }

        // Use NeighborMessage helper (handles SubmodelElementCollection parsing).
        return NeighborMessage.GetNeighbors(message.InteractionElements.ToList());
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic subscription node.
    /// - Dispatching roles subscribe to dispatching-wide topics.
    /// - All other roles subscribe to module-holon style topics (including /{ns}/{moduleId}/register).
    ///
    /// Designed to replace NodeRegistry aliases so BTs can consistently reference `SubscribeAgentTopics`.
    /// </summary>
    public class SubscribeAgentTopicsNode : BTNode
    {
        public string Role { get; set; } = string.Empty;

        public SubscribeAgentTopicsNode() : base("SubscribeAgentTopics") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("SubscribeAgentTopics: MessagingClient unavailable or disconnected");
                return NodeStatus.Failure;
            }

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var role = !string.IsNullOrWhiteSpace(Role) ? ResolveTemplates(Role) : (Context.AgentRole ?? string.Empty);

            if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                // Minimal dispatching subscriptions (plus registration topics)
                var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    $"/{ns}/ProcessChain",
                    $"/{ns}/request/ManufacturingSequence",
                    $"/{ns}/request/BookStep",
                    $"/{ns}/TransportPlan",

                    // Offer request/response topics (CfP broadcast + module proposals)
                    $"/{ns}/Offer",

                    $"/{ns}/register",
                    $"/{ns}/Register",
                    $"/{ns}/ModuleRegistration",

                    // Inventory updates are published per module: /{ns}/{moduleId}/Inventory
                    // Use MQTT wildcard subscription so the dispatcher can aggregate inventories.
                    $"/{ns}/+/Inventory"
                };

                var ok = 0;
                foreach (var topic in topics)
                {
                    try
                    {
                        await client.SubscribeAsync(topic).ConfigureAwait(false);
                        ok++;
                        Logger.LogInformation("SubscribeAgentTopics: subscribed {Topic}", topic);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "SubscribeAgentTopics: failed to subscribe {Topic}", topic);
                    }
                }

                // Ensure we update DispatchingState from incoming messages even if a BT loop misses it.
                if (!Context.Get<bool>("DispatchingTopicsHandlerRegistered"))
                {
                    client.OnMessage(m =>
                    {
                        try
                        {
                            if (m == null) return;

                            var type = m.Frame?.Type ?? string.Empty;
                            var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();

                            if (string.Equals(type, "registerMessage", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(type, "moduleRegistration", StringComparison.OrdinalIgnoreCase))
                            {
                                var now = DateTime.UtcNow;
                                var info = DispatchingModuleInfo.FromMessage(m);
                                if (!string.IsNullOrWhiteSpace(info.ModuleId))
                                {
                                    info.LastRegistrationUtc = now;
                                    info.LastSeenUtc = now;
                                    state.Upsert(info);
                                    Context.Set("DispatchingState", state);
                                    Logger.LogDebug("SubscribeAgentTopics: upserted module {ModuleId} (caps={Count})", info.ModuleId, info.Capabilities?.Count ?? 0);
                                }
                                return;
                            }

                            if (string.Equals(type, "inventoryUpdate", StringComparison.OrdinalIgnoreCase)
                                || InventoryMessage.ContainsStorageUnits(m.InteractionElements))
                            {
                                var senderId = m.Frame?.Sender?.Identification?.Id ?? string.Empty;
                                var moduleId = NormalizeModuleId(senderId);
                                if (!string.IsNullOrWhiteSpace(moduleId)
                                    && TryExtractInventorySummary(m.InteractionElements, out var free, out var occupied))
                                {
                                    state.UpsertInventory(moduleId, free, occupied, seenAtUtc: DateTime.UtcNow);
                                    Context.Set("DispatchingState", state);
                                    Logger.LogDebug("SubscribeAgentTopics: updated inventory for {ModuleId} free={Free} occupied={Occupied}", moduleId, free, occupied);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "SubscribeAgentTopics: dispatching message handler failed");
                        }
                    });

                    Context.Set("DispatchingTopicsHandlerRegistered", true);
                }

                return ok > 0 ? NodeStatus.Success : NodeStatus.Failure;
            }

            // Non-dispatching: reuse the established module holon topic conventions
            var primaryModuleId = ModuleContextHelper.ResolveModuleId(Context);
            var moduleIdentifiers = ModuleContextHelper.ResolveModuleIdentifiers(Context);
            var moduleTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"/{ns}/Offer",
                $"/{ns}/TransportPlan"
            };

            foreach (var moduleId in moduleIdentifiers)
            {
                moduleTopics.Add($"/{ns}/{moduleId}/ScheduleAction");
                moduleTopics.Add($"/{ns}/{moduleId}/BookingConfirmation");
                moduleTopics.Add($"/{ns}/{moduleId}/TransportPlan");
                moduleTopics.Add($"/{ns}/{moduleId}/PlanningAgent/OfferResponse");
                moduleTopics.Add($"/{ns}/{moduleId}/register");
                moduleTopics.Add($"/{ns}/{moduleId}/Inventory");
                moduleTopics.Add($"/{ns}/{moduleId}/Neighbors");
            }

            var success = 0;
            foreach (var topic in moduleTopics)
            {
                try
                {
                    await client.SubscribeAsync(topic).ConfigureAwait(false);
                    success++;
                    Logger.LogInformation("SubscribeAgentTopics: subscribed {Topic}", topic);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "SubscribeAgentTopics: failed to subscribe {Topic}", topic);
                }
            }

            // Keep the same caching behavior as SubscribeModuleHolonTopicsNode (idempotent)
            if (!Context.Get<bool>("ModuleHolonTopicsHandlerRegistered"))
            {
                client.OnMessage(m =>
                {
                    if (m == null) return;

                    if (IsInventoryUpdate(m))
                    {
                        var cache = BuildInventoryCache(m);
                        if (cache != null)
                        {
                            Context.Set($"InventoryCache_{primaryModuleId}", cache);
                            Context.Set("ModuleInventory", cache.StorageUnits);
                        }
                    }
                    else if (IsNeighborsUpdate(m))
                    {
                        var cache = BuildNeighborsCache(m);
                        if (cache != null)
                        {
                            Context.Set($"NeighborsCache_{primaryModuleId}", cache);
                            Context.Set("Neighbors", cache.Neighbors);
                        }
                    }
                });
                Context.Set("ModuleHolonTopicsHandlerRegistered", true);
            }

            return success > 0 ? NodeStatus.Success : NodeStatus.Failure;
        }

        private static string NormalizeModuleId(string senderId)
        {
            if (string.IsNullOrWhiteSpace(senderId)) return string.Empty;

            if (senderId.EndsWith("_Execution", StringComparison.OrdinalIgnoreCase))
            {
                return senderId.Substring(0, senderId.Length - "_Execution".Length);
            }

            if (senderId.EndsWith("_Planning", StringComparison.OrdinalIgnoreCase))
            {
                return senderId.Substring(0, senderId.Length - "_Planning".Length);
            }

            return senderId;
        }

        private static bool TryExtractInventorySummary(IEnumerable<ISubmodelElement>? elements, out int free, out int occupied)
        {
            free = 0;
            occupied = 0;

            if (elements == null) return false;

            // Prefer embedded summary inside StorageUnits
            var storageUnits = elements
                .OfType<SubmodelElementCollection>()
                .FirstOrDefault(c => string.Equals(c.IdShort, "StorageUnits", StringComparison.OrdinalIgnoreCase));

            if (storageUnits != null)
            {
                    var summary = AasValueUnwrap.UnwrapToEnumerable<ISubmodelElement>(storageUnits.Value)
                    .OfType<SubmodelElementCollection>()
                    .FirstOrDefault(c => string.Equals(c.IdShort, "InventorySummary", StringComparison.OrdinalIgnoreCase));

                if (summary != null && TryReadFreeOccupied(summary, out free, out occupied))
                {
                    return true;
                }
            }

            // Fallback: top-level summary
            var topLevel = elements
                .OfType<SubmodelElementCollection>()
                .FirstOrDefault(c => string.Equals(c.IdShort, "InventorySummary", StringComparison.OrdinalIgnoreCase));

            if (topLevel != null && TryReadFreeOccupied(topLevel, out free, out occupied))
            {
                return true;
            }

            return false;
        }

        private static bool TryReadFreeOccupied(SubmodelElementCollection summary, out int free, out int occupied)
        {
            free = 0;
            occupied = 0;

                var values = AasValueUnwrap.UnwrapToEnumerable<ISubmodelElement>(summary.Value);
            foreach (var el in values.OfType<Property>())
            {
                if (string.Equals(el.IdShort, "free", StringComparison.OrdinalIgnoreCase))
                {
                    free = TryToInt(el) ?? free;
                }
                else if (string.Equals(el.IdShort, "occupied", StringComparison.OrdinalIgnoreCase))
                {
                    occupied = TryToInt(el) ?? occupied;
                }
            }

            return true;
        }

        private static int? TryToInt(Property p)
        {
            try
            {
                 return AasValueUnwrap.UnwrapToInt(p.Value);
            }
            catch
            {
                try
                {
                    var str = p.Value?.ToString();
                    if (int.TryParse(str, out var parsed)) return parsed;
                }
                catch { }
            }

            return null;
        }

        private string ResolveTemplates(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.Contains('{')) return value;

            var result = value;
            var startIndex = 0;
            while (startIndex < result.Length)
            {
                var openBrace = result.IndexOf('{', startIndex);
                if (openBrace == -1) break;
                var closeBrace = result.IndexOf('}', openBrace);
                if (closeBrace == -1) break;

                var placeholder = result.Substring(openBrace + 1, closeBrace - openBrace - 1);
                var replacement = Context.Get<string>(placeholder) ?? $"{{{placeholder}}}";
                result = result.Substring(0, openBrace) + replacement + result.Substring(closeBrace + 1);
                startIndex = openBrace + replacement.Length;
            }

            return result;
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
            if (message == null) return false;
            var type = message.Frame?.Type;
            if (string.Equals(type, "inventoryUpdate", StringComparison.OrdinalIgnoreCase)) return true;
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

            return NeighborMessage.GetNeighbors(message.InteractionElements.ToList());
        }
    }
}

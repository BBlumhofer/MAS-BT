using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Utilities;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic subscription node with role-based topic patterns.
    /// Uses the new generalized topic pattern: /{ns}/{targetRole}/broadcast/{MessageType}
    /// 
    /// - Dispatching: namespace-level requests and responses
    /// - ModuleHolon: role-based broadcasts and direct module topics
    /// - PlanningHolon: only parent module's internal topics
    /// - ExecutionHolon: only parent module's skill execution topics
    /// - TransportManager: transport-specific topics
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

            EnsureCapabilityAggregationHandler(client);

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var role = !string.IsNullOrWhiteSpace(Role) ? ResolveTemplates(Role) : (Context.AgentRole ?? string.Empty);
            var agentId = Context.Get<string>("config.Agent.AgentId") ?? Context.AgentId ?? string.Empty;

            HashSet<string> topics;

            // Role-specific topic selection
            if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                topics = BuildDispatchingTopics(ns, agentId);
                return await SubscribeDispatchingTopics(client, topics, ns);
            }
            else if (role.Contains("ModuleHolon", StringComparison.OrdinalIgnoreCase) 
                     && !role.Contains("Planning", StringComparison.OrdinalIgnoreCase)
                     && !role.Contains("Execution", StringComparison.OrdinalIgnoreCase))
            {
                var moduleId = Context.Get<string>("config.Agent.ModuleId") ?? agentId;
                topics = BuildModuleHolonTopics(ns, moduleId);
                return await SubscribeModuleHolonTopics(client, topics, ns, moduleId);
            }
            else if (role.Contains("Planning", StringComparison.OrdinalIgnoreCase))
            {
                var parentModuleId = Context.Get<string>("config.Agent.ModuleId");
                if (string.IsNullOrWhiteSpace(parentModuleId))
                {
                    Logger.LogError("SubscribeAgentTopics: PlanningHolon requires config.Agent.ModuleId (parent module ID)");
                    return NodeStatus.Failure;
                }
                topics = BuildPlanningHolonTopics(ns, parentModuleId);
            }
            else if (role.Contains("Execution", StringComparison.OrdinalIgnoreCase))
            {
                var parentModuleId = Context.Get<string>("config.Agent.ModuleId");
                if (string.IsNullOrWhiteSpace(parentModuleId))
                {
                    Logger.LogError("SubscribeAgentTopics: ExecutionHolon requires config.Agent.ModuleId (parent module ID)");
                    return NodeStatus.Failure;
                }
                topics = BuildExecutionHolonTopics(ns, parentModuleId);
            }
            else if (role.Contains("TransportManager", StringComparison.OrdinalIgnoreCase))
            {
                topics = BuildTransportManagerTopics(ns);
            }
            else
            {
                Logger.LogWarning("SubscribeAgentTopics: Unknown role '{Role}', using minimal default topics", role);
                topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"/{ns}/register" };
            }

            // Generic subscription for non-dispatching, non-ModuleHolon roles
            return await SubscribeGenericTopics(client, topics);
        }

        private HashSet<string> BuildDispatchingTopics(string ns, string agentId)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // External requests (from products)
                $"/{ns}/ManufacturingSequence/Request",
                $"/{ns}/BookStep/Request",

                // Direct responses to this dispatcher (NEW PATTERN)
                $"/{ns}/{agentId}/OfferedCapability/Response",
                $"/{ns}/{agentId}/ManufacturingSequence/Response",

                // Registration topics (all agents register with dispatcher)
                $"/{ns}/register",
                $"/{ns}/Register",
                $"/{ns}/NamespaceHolon/register",
                $"/{ns}/NamespaceHolon/Register",
                $"/{ns}/ModuleRegistration",
                $"/{ns}/+/register",            // Wildcard: all module/sub-agent registrations

                // Inventory aggregation (from all modules)
                $"/{ns}/+/Inventory"
            };

        }

        private HashSet<string> BuildModuleHolonTopics(string ns, string moduleId)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Role-based broadcasts from Dispatcher (NEW PATTERN)
                $"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request",
                $"/{ns}/ModuleHolon/broadcast/TransportPlan/Request",

                // Direct messages to this specific module
                $"/{ns}/{moduleId}/ScheduleAction",
                $"/{ns}/{moduleId}/BookingConfirmation",
                $"/{ns}/{moduleId}/TransportPlan",
                $"/{ns}/{moduleId}/register",          // Sub-holon registrations
                $"/{ns}/{moduleId}/Neighbors"          // Neighbor updates
            };
        }

        private HashSet<string> BuildPlanningHolonTopics(string ns, string parentModuleId)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // ONLY internal topics from parent ModuleHolon
                $"/{ns}/{parentModuleId}/Planning/OfferedCapability/Request",
                $"/{ns}/{parentModuleId}/Planning/ScheduleAction",
                $"/{ns}/{parentModuleId}/Planning/TransportRequest",

                // Direct responses from TransportManager
                $"/{ns}/TransportPlan/Response"
            };
        }

        private HashSet<string> BuildExecutionHolonTopics(string ns, string parentModuleId)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // ONLY internal topics from parent ModuleHolon
                $"/{ns}/{parentModuleId}/Execution/SkillRequest"
            };
        }

        private HashSet<string> BuildTransportManagerTopics(string ns)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Transport requests from planning agents
                $"/{ns}/TransportPlan/Request"
            };
        }

        private async Task<NodeStatus> SubscribeDispatchingTopics(MessagingClient client, HashSet<string> topics, string ns)
        {
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

            // Register message handler for dispatching state updates
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

        private async Task<NodeStatus> SubscribeModuleHolonTopics(MessagingClient client, HashSet<string> topics, string ns, string moduleId)
        {
            var success = 0;
            foreach (var topic in topics)
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

            // Register message handler for inventory/neighbor caching
            if (!Context.Get<bool>("ModuleHolonTopicsHandlerRegistered"))
            {
                client.OnMessage(m =>
                {
                    if (m == null) return;

                    var type = m.Frame?.Type ?? string.Empty;
                    if (string.Equals(type, "registerMessage", StringComparison.OrdinalIgnoreCase))
                    {
                        TrackModuleCapabilities(m);
                    }
                    else if (IsInventoryUpdate(m))
                    {
                        var cache = BuildInventoryCache(m);
                        if (cache != null)
                        {
                            Context.Set($"InventoryCache_{moduleId}", cache);
                            Context.Set("ModuleInventory", cache.StorageUnits);
                        }
                    }
                    else if (IsNeighborsUpdate(m))
                    {
                        var cache = BuildNeighborsCache(m);
                        if (cache != null)
                        {
                            Context.Set($"NeighborsCache_{moduleId}", cache);
                            Context.Set("Neighbors", cache.Neighbors);
                        }
                    }
                });
                Context.Set("ModuleHolonTopicsHandlerRegistered", true);
            }

            return success > 0 ? NodeStatus.Success : NodeStatus.Failure;
        }

        private void EnsureCapabilityAggregationHandler(MessagingClient client)
        {
            if (Context.Get<bool>("CapabilityAggregationHandlerRegistered"))
            {
                return;
            }

            var expectedReceiverId = ResolveRegistrationReceiverId();

            client.OnMessage(m =>
            {
                if (m == null)
                {
                    return;
                }

                var type = m.Frame?.Type ?? string.Empty;
                if (string.Equals(type, "registerMessage", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "moduleRegistration", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(expectedReceiverId))
                    {
                        TrackModuleCapabilities(m);
                        return;
                    }

                    var receiverId = m.Frame?.Receiver?.Identification?.Id ?? string.Empty;
                    if (string.Equals(receiverId, expectedReceiverId, StringComparison.OrdinalIgnoreCase))
                    {
                        TrackModuleCapabilities(m);
                    }
                }
            });

            Context.Set("CapabilityAggregationHandlerRegistered", true);
        }

        private string ResolveRegistrationReceiverId()
        {
            var candidate = Context.AgentId
                            ?? Context.Get<string>("AgentId")
                            ?? Context.Get<string>("config.Agent.AgentId")
                            ?? Context.Get<string>("config.Agent.Id")
                            ?? string.Empty;
            return ResolveTemplates(candidate).Trim();
        }
        private void TrackModuleCapabilities(I40Message message)
        {
            var capList = ExtractCapabilitiesFromRegisterMessage(message);
            if (capList.Count == 0)
            {
                return;
            }

            var existingValues = Context.Get<List<string>>("Capabilities") ?? new List<string>();
            var existing = new HashSet<string>(existingValues, StringComparer.OrdinalIgnoreCase);

            foreach (var cap in capList)
            {
                existing.Add(cap);
            }

            var aggregated = existing.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            Context.Set("Capabilities", aggregated);
            Context.Set("CapabilityNames", aggregated);
            Context.Set(RegistrationContextKeys.CapabilitiesExtractionComplete, true);
        }

        private static List<string> ExtractCapabilitiesFromRegisterMessage(I40Message message)
        {
            var result = new List<string>();
            if (message?.InteractionElements == null)
            {
                return result;
            }

            var registerCollections = message.InteractionElements
                .OfType<SubmodelElementCollection>()
                .Where(c => string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase));

            foreach (var collection in registerCollections)
            {
                try
                {
                    var reg = RegisterMessage.FromSubmodelElementCollection(collection);
                    if (reg.Capabilities != null)
                    {
                        foreach (var cap in reg.Capabilities)
                        {
                            if (!string.IsNullOrWhiteSpace(cap))
                            {
                                result.Add(cap.Trim());
                            }
                        }
                    }
                }
                catch
                {
                    // best effort
                }
            }

            return result;
        }

        private async Task<NodeStatus> SubscribeGenericTopics(MessagingClient client, HashSet<string> topics)
        {
            var success = 0;
            foreach (var topic in topics)
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

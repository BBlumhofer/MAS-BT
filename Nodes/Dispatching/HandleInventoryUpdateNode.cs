using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.Helpers;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching
{
    /// <summary>
    /// Handles inventoryUpdate messages and updates DispatchingState with aggregated free/occupied counts per module.
    /// Expects Context[LastReceivedMessage] to contain an inventoryUpdate message.
    /// </summary>
    public class HandleInventoryUpdateNode : BTNode
    {
        public HandleInventoryUpdateNode() : base("HandleInventoryUpdate") { }

        public override Task<NodeStatus> Execute()
        {
            var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
            Context.Set("DispatchingState", state);

            var message = Context.Get<I40Message>("LastReceivedMessage");
            if (message == null)
            {
                Logger.LogWarning("HandleInventoryUpdate: no message in context");
                return Task.FromResult(NodeStatus.Failure);
            }

            var senderId = message.Frame?.Sender?.Identification?.Id ?? string.Empty;
            var moduleId = NormalizeModuleId(senderId);
            if (string.IsNullOrWhiteSpace(moduleId))
            {
                Logger.LogWarning("HandleInventoryUpdate: unable to infer module id from sender '{Sender}'", senderId);
                return Task.FromResult(NodeStatus.Failure);
            }

            if (!TryExtractSummary(message.InteractionElements, out var free, out var occupied))
            {
                Logger.LogDebug("HandleInventoryUpdate: no InventorySummary found in message from {Sender}", senderId);
                return Task.FromResult(NodeStatus.Success);
            }

            state.UpsertInventory(moduleId, free, occupied, seenAtUtc: DateTime.UtcNow);
            Context.Set("DispatchingState", state);

            Logger.LogDebug(
                "HandleInventoryUpdate: updated inventory for {ModuleId}: free={Free} occupied={Occupied}",
                moduleId,
                free,
                occupied);

            return Task.FromResult(NodeStatus.Success);
        }

        private static string NormalizeModuleId(string senderId)
        {
            if (string.IsNullOrWhiteSpace(senderId)) return string.Empty;

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

        private static bool TryExtractSummary(IEnumerable<ISubmodelElement>? elements, out int free, out int occupied)
        {
            free = 0;
            occupied = 0;

            if (elements == null) return false;

            // Prefer embedded summary inside StorageUnits collection
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

            // Fallback: allow summary as top-level interaction element
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

            static int? ReadInt(Property p)
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
                    catch { /* ignore */ }
                }

                return null;
            }

            foreach (var el in values.OfType<Property>())
            {
                if (string.Equals(el.IdShort, "free", StringComparison.OrdinalIgnoreCase))
                {
                    free = ReadInt(el) ?? free;
                }
                else if (string.Equals(el.IdShort, "occupied", StringComparison.OrdinalIgnoreCase))
                {
                    occupied = ReadInt(el) ?? occupied;
                }
            }

            return true;
        }
    }
}

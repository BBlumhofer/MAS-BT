using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using AAS_Sharp_Client.Models.Messages;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Models;

namespace MAS_BT
{
    /// <summary>
    /// Registration information for a module known to the dispatching agent.
    /// Minimal local copy to satisfy MAS-BT usage.
    /// </summary>
    public class DispatchingModuleInfo
    {
        public string ModuleId { get; set; } = string.Empty;
        public string? AasId { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public List<string> Neighbors { get; set; } = new();
        public int InventoryFree { get; set; } = 0;
        public int InventoryOccupied { get; set; } = 0;
        public DateTime LastRegistrationUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        public static DispatchingModuleInfo FromMessage(object? message)
        {
            var now = DateTime.UtcNow;
            var info = new DispatchingModuleInfo { LastRegistrationUtc = now, LastSeenUtc = now };

            if (message is not I40Message m)
            {
                return info;
            }

            info.ModuleId = m.Frame?.Sender?.Identification?.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(info.ModuleId))
            {
                info.ModuleId = m.Frame?.Receiver?.Identification?.Id ?? string.Empty;
            }

            var elements = m.InteractionElements?.ToList() ?? new List<ISubmodelElement>();

            // RegisterMessage (standardized)
            var regCollection = elements
                .OfType<SubmodelElementCollection>()
                .FirstOrDefault(c => string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase));

            if (regCollection != null)
            {
                try
                {
                    var reg = RegisterMessage.FromSubmodelElementCollection(regCollection);
                    if (!string.IsNullOrWhiteSpace(reg.AgentId))
                    {
                        info.ModuleId = reg.AgentId;
                    }

                    if (reg.Capabilities != null && reg.Capabilities.Count > 0)
                    {
                        info.Capabilities = reg.Capabilities
                            .Where(c => !string.IsNullOrWhiteSpace(c))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
                catch
                {
                    // fall back to sender id only
                }
            }

            // Optional neighbors snapshot if present
            if (elements.Count > 0)
            {
                try
                {
                    var neighbors = NeighborMessage.GetNeighbors(elements);
                    if (neighbors.Count > 0)
                    {
                        info.Neighbors = neighbors;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return info;
        }
    }

    /// <summary>
    /// In-memory registry/state for dispatching, indexing modules by capability.
    /// </summary>
    public class DispatchingState
    {
        private readonly Dictionary<string, DispatchingModuleInfo> _modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _capabilityIndex = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, string> _capabilityDescriptions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, double> _capabilitySimilarityCache = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<DispatchingModuleInfo> Modules => _modules.Values;

        public bool TryGetCapabilityDescription(string capability, out string description)
        {
            description = string.Empty;
            if (string.IsNullOrWhiteSpace(capability))
            {
                return false;
            }
            if (_capabilityDescriptions.TryGetValue(capability.Trim(), out var stored) && stored != null)
            {
                description = stored;
                return true;
            }

            return false;
        }

        public void SetCapabilityDescription(string capability, string description)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                return;
            }
            _capabilityDescriptions[capability.Trim()] = description ?? string.Empty;
        }

        public bool TryGetCapabilitySimilarity(string capabilityA, string capabilityB, out double similarity)
        {
            similarity = 0.0;
            if (string.IsNullOrWhiteSpace(capabilityA) || string.IsNullOrWhiteSpace(capabilityB))
            {
                return false;
            }
            var key = MakeCapabilityPairKey(capabilityA, capabilityB);
            return _capabilitySimilarityCache.TryGetValue(key, out similarity);
        }

        public void SetCapabilitySimilarity(string capabilityA, string capabilityB, double similarity)
        {
            if (string.IsNullOrWhiteSpace(capabilityA) || string.IsNullOrWhiteSpace(capabilityB))
            {
                return;
            }
            var key = MakeCapabilityPairKey(capabilityA, capabilityB);
            _capabilitySimilarityCache[key] = similarity;
        }

        private static string MakeCapabilityPairKey(string capabilityA, string capabilityB)
        {
            var a = capabilityA.Trim();
            var b = capabilityB.Trim();
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
                ? $"{a}||{b}"
                : $"{b}||{a}";
        }

        public void Upsert(DispatchingModuleInfo module)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleId))
                return;

            if (_modules.TryGetValue(module.ModuleId, out var existing))
            {
                RemoveFromIndex(existing);

                // Registration updates typically do not include inventory.
                // Preserve the last known inventory snapshot to avoid brief 0/0 resets
                // when a module sends a periodic registration heartbeat.
                module.InventoryFree = existing.InventoryFree;
                module.InventoryOccupied = existing.InventoryOccupied;
            }

            _modules[module.ModuleId] = module;
            AddToIndex(module);
        }

        public void UpsertInventory(string moduleId, int free, int occupied, DateTime? seenAtUtc = null)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
            {
                return;
            }

            var now = seenAtUtc ?? DateTime.UtcNow;

            if (!_modules.TryGetValue(moduleId, out var existing))
            {
                existing = new DispatchingModuleInfo { ModuleId = moduleId };
            }
            else
            {
                // keep capability index consistent if we replace
                RemoveFromIndex(existing);
            }

            existing.InventoryFree = free;
            existing.InventoryOccupied = occupied;
            existing.LastSeenUtc = now;

            _modules[moduleId] = existing;
            AddToIndex(existing);
        }

        public List<string> PruneStaleModules(TimeSpan timeout, DateTime? nowUtc = null, string? excludeModuleId = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (timeout <= TimeSpan.Zero)
            {
                return new List<string>();
            }

            var staleIds = _modules.Values
                .Where(m => !string.IsNullOrWhiteSpace(m.ModuleId))
                .Where(m => string.IsNullOrWhiteSpace(excludeModuleId) || !string.Equals(m.ModuleId, excludeModuleId, StringComparison.OrdinalIgnoreCase))
                .Where(m => now - m.LastSeenUtc > timeout)
                .Select(m => m.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var id in staleIds)
            {
                if (_modules.TryGetValue(id, out var existing))
                {
                    RemoveFromIndex(existing);
                    _modules.Remove(id);
                }
            }

            return staleIds;
        }

        public IReadOnlyCollection<string> FindModulesForCapability(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability))
                return _modules.Keys.ToList();

            if (_capabilityIndex.TryGetValue(capability, out var set))
                return set.ToList();

            return Array.Empty<string>();
        }

        public IReadOnlyCollection<string> AllModuleIds() => _modules.Keys.ToList();

        private void AddToIndex(DispatchingModuleInfo module)
        {
            foreach (var cap in module.Capabilities.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                if (!_capabilityIndex.TryGetValue(cap, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _capabilityIndex[cap] = set;
                }
                set.Add(module.ModuleId);
            }
        }

        private void RemoveFromIndex(DispatchingModuleInfo module)
        {
            foreach (var cap in module.Capabilities.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                if (_capabilityIndex.TryGetValue(cap, out var set))
                {
                    set.Remove(module.ModuleId);
                    if (set.Count == 0)
                        _capabilityIndex.Remove(cap);
                }
            }
        }
    }
}

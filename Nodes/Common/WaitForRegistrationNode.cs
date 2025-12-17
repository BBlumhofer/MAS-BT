using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Nodes.Planning;
using MAS_BT.Tools;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic wait-for-registration node.
    /// Subscribes to /{ns}/{moduleId}/register and waits for a registration message.
    /// Optionally filters by ExpectedAgents (comma-separated or JSON array string).
    /// </summary>
    public class WaitForRegistrationNode : BTNode
    {
        public string ExpectedAgents { get; set; } = string.Empty;
        public string ExpectedTypes { get; set; } = "subHolonRegister";
        public int TimeoutSeconds { get; set; } = 10;
        public string Namespace { get; set; } = string.Empty;

        /// <summary>
        /// Optional explicit expected count. When 0, we will try to infer it:
        /// - ExpectedAgents count (if provided)
        /// - config.SubHolons array length (if present)
        /// - otherwise 1
        /// </summary>
        public int ExpectedCount { get; set; } = 0;

        public WaitForRegistrationNode() : base("WaitForRegistration") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("WaitForRegistration: MessagingClient unavailable");
                return NodeStatus.Failure;
            }

            var ns = !string.IsNullOrWhiteSpace(Namespace)
                ? ResolveTemplates(Namespace)
                : (Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket");
            var moduleId = ModuleContextHelper.ResolveModuleId(Context);
            var topic = $"/{ns}/{moduleId}/register";

            var expectedTypeSet = ParseCsvOrDefault(ResolveTemplates(ExpectedTypes), new[] { "subHolonRegister" });
            var expectedAgents = ParseExpectedAgents(ResolveTemplates(ExpectedAgents));

            var expectedCount = ResolveExpectedCount(expectedAgents);
            if (expectedCount < 1)
            {
                expectedCount = 1;
            }

            Logger.LogInformation("WaitForRegistration: waiting for {Count} registrations on {Topic}", expectedCount, topic);

            var queue = new ConcurrentQueue<I40Message>();
            await client.SubscribeAsync(topic).ConfigureAwait(false);
            client.OnMessage(m =>
            {
                if (m?.Frame?.Type == null)
                {
                    return;
                }

                if (!expectedTypeSet.Contains(m.Frame.Type))
                {
                    return;
                }

                if (expectedAgents.Count > 0)
                {
                    var senderId = m.Frame?.Sender?.Identification?.Id ?? string.Empty;
                    if (!expectedAgents.Contains(senderId))
                    {
                        return;
                    }
                }

                queue.Enqueue(m);
            });

            var start = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (DateTime.UtcNow - start < timeout)
            {
                if (queue.TryDequeue(out var msg))
                {
                    Context.Set("LastReceivedMessage", msg);
                    await TrySyncRegistrationToNeo4jAsync().ConfigureAwait(false);

                    var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
                    var info = DispatchingModuleInfo.FromMessage(msg);
                    if (string.IsNullOrWhiteSpace(info.ModuleId))
                    {
                        info.ModuleId = msg.Frame?.Sender?.Identification?.Id ?? string.Empty;
                    }
                    info.LastRegistrationUtc = DateTime.UtcNow;
                    info.LastSeenUtc = info.LastRegistrationUtc;
                    state.Upsert(info);
                    Context.Set("DispatchingState", state);

                    var seenKey = BuildSeenKey(msg, info);
                    if (!string.IsNullOrWhiteSpace(seenKey))
                    {
                        seen.Add(seenKey);
                    }

                    if (info.Capabilities != null)
                    {
                        foreach (var c in info.Capabilities)
                        {
                            if (!string.IsNullOrWhiteSpace(c))
                            {
                                seenCapabilities.Add(c.Trim());
                            }
                        }
                    }

                    Logger.LogInformation("WaitForRegistration: received registration from {Id} ({Seen}/{Expected})",
                        DescribeSource(msg, info),
                        seen.Count,
                        expectedCount);

                    if (seen.Count >= expectedCount)
                    {
                        // Make aggregated capabilities available to downstream nodes (e.g. ModuleHolon RegisterAgent).
                        // Only set if not already present / non-empty.
                        try
                        {
                            var existingCaps = Context.Has("Capabilities") ? Context.Get<List<string>>("Capabilities") : null;
                            var existingNames = Context.Has("CapabilityNames") ? Context.Get<List<string>>("CapabilityNames") : null;
                            if ((existingCaps == null || existingCaps.Count == 0) && (existingNames == null || existingNames.Count == 0))
                            {
                                var list = seenCapabilities
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                Context.Set("Capabilities", list);
                                Context.Set("CapabilityNames", list);
                                if (list.Count > 0)
                                {
                                    Logger.LogInformation("WaitForRegistration: aggregated {Count} capabilities for downstream registration", list.Count);
                                }
                            }
                        }
                        catch
                        {
                            // best-effort
                        }

                        return NodeStatus.Success;
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            Logger.LogWarning("WaitForRegistration: timeout waiting for registration on {Topic}", topic);
            return NodeStatus.Failure;
        }

        private static string BuildSeenKey(I40Message msg, DispatchingModuleInfo info)
        {
            var id = info.ModuleId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = msg.Frame?.Sender?.Identification?.Id ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            // When Planning/Execution sub-holons share the same AgentId (e.g. "P103"),
            // we must differentiate them to reach ExpectedCount=2.
            var role = msg.Frame?.Sender?.Role?.Name ?? string.Empty;

            if (role.Contains("Planning", StringComparison.OrdinalIgnoreCase))
            {
                return $"{id}:Planning";
            }
            if (role.Contains("Execution", StringComparison.OrdinalIgnoreCase))
            {
                return $"{id}:Execution";
            }

            // Fallback: count by id only.
            return id;
        }

        private static string DescribeSource(I40Message msg, DispatchingModuleInfo info)
        {
            var id = info.ModuleId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = msg.Frame?.Sender?.Identification?.Id ?? string.Empty;
            }

            var role = msg.Frame?.Sender?.Role?.Name ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(id))
            {
                return $"{id} ({role})";
            }

            return string.IsNullOrWhiteSpace(id) ? "<unknown>" : id;
        }

        private async Task TrySyncRegistrationToNeo4jAsync()
        {
            try
            {
                if (!Context.Has("Neo4jDriver"))
                {
                    return;
                }

                var syncNode = new SyncAgentToNeo4jNode { Context = Context };
                syncNode.SetLogger(Logger);
                var status = await syncNode.Execute().ConfigureAwait(false);
                if (status != NodeStatus.Success)
                {
                    Logger.LogDebug("WaitForRegistration: SyncAgentToNeo4j returned {Status}", status);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "WaitForRegistration: failed to sync registration to Neo4j");
            }
        }

        private int ResolveExpectedCount(HashSet<string> expectedAgents)
        {
            if (ExpectedCount > 0)
            {
                return ExpectedCount;
            }

            if (expectedAgents.Count > 0)
            {
                return expectedAgents.Count;
            }

            // Try infer from config.SubHolons (commonly a JSON array)
            try
            {
                var elem = Context.Get<object>("config.SubHolons");
                if (elem is IList<object?> list)
                {
                    if (list.Count > 0)
                    {
                        return list.Count;
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Backward compat: some configs store SubHolons under config.Agent.SubHolons
            try
            {
                var elem = Context.Get<object>("config.Agent.SubHolons");
                if (elem is IList<object?> list)
                {
                    if (list.Count > 0)
                    {
                        return list.Count;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return 1;
        }

        private static HashSet<string> ParseCsvOrDefault(string? csv, IEnumerable<string> defaults)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(csv))
            {
                foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    set.Add(part);
                }
            }

            if (set.Count == 0)
            {
                foreach (var d in defaults) set.Add(d);
            }

            return set;
        }

        private HashSet<string> ParseExpectedAgents(string? value)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value))
            {
                return set;
            }

            // Unresolved placeholders should not become a hard filter.
            if (value.Contains('{') || value.Contains('}'))
            {
                return set;
            }

            // JSON array (common when coming from {config.SubHolons})
            var trimmed = value.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                try
                {
                    var parsed = JsonFacade.Parse(trimmed);
                    if (parsed is IList<object?> list)
                    {
                        foreach (var el in list)
                        {
                            var s = el as string ?? JsonFacade.ToStringValue(el);
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                set.Add(s);
                            }
                        }
                    }

                    return set;
                }
                catch
                {
                    // fall back to CSV parsing
                }
            }

            foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part);
            }

            return set;
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
                // Prefer string values, but allow JSON arrays/objects (e.g. config.SubHolons) to be used as JSON text.
                var raw = Context.Get<object>(placeholder);
                var replacement = raw as string;
                if (replacement == null && (raw is IDictionary<string, object?> || raw is IList<object?>))
                {
                    replacement = JsonFacade.Serialize(raw);
                }

                replacement ??= JsonFacade.ToStringValue(raw);

                replacement ??= $"{{{placeholder}}}";
                result = result.Substring(0, openBrace) + replacement + result.Substring(closeBrace + 1);
                startIndex = openBrace + replacement.Length;
            }

            return result;
        }
    }
}

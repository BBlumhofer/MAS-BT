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
        public string ExpectedAgentsPath { get; set; } = string.Empty;
        public string ExpectedTypes { get; set; } = "registerMessage";
        public int TimeoutSeconds { get; set; } = 10;
        public string Namespace { get; set; } = string.Empty;
        public string TopicOverride { get; set; } = string.Empty;

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
            var topic = !string.IsNullOrWhiteSpace(TopicOverride)
                ? ResolveTemplates(TopicOverride)
                : $"/{ns}/{moduleId}/register";

            // Subscribe only to the exact register topic to stay scoped to this agent
            var subscribeTopic = topic;

            var expectedTypeSet = ParseCsvOrDefault(ResolveTemplates(ExpectedTypes), new[] { "registerMessage" });
            var expectedAgents = ParseExpectedAgents(ResolveTemplates(ExpectedAgents));

            if (expectedAgents.Count == 0 && !string.IsNullOrWhiteSpace(ExpectedAgentsPath))
            {
                var fromPath = ResolveExpectedAgentsFromPath(ExpectedAgentsPath);
                foreach (var agent in fromPath)
                {
                    expectedAgents.Add(agent);
                }

                if (fromPath.Count == 0)
                {
                    Logger.LogDebug(
                        "WaitForRegistration: ExpectedAgentsPath '{Path}' resolved to empty set",
                        ExpectedAgentsPath);
                }
            }

            var expectedCount = ResolveExpectedCount(expectedAgents);
            if (expectedCount < 1)
            {
                expectedCount = 1;
            }

            Logger.LogInformation(
                "WaitForRegistration: waiting for {Count} registrations on {Topic} (expected agents: {Agents}, types: {Types})",
                expectedCount,
                topic,
                expectedAgents.Count > 0 ? string.Join(",", expectedAgents) : "<any>",
                string.Join(",", expectedTypeSet));

            var queue = new ConcurrentQueue<I40Message>();
            await client.SubscribeAsync(subscribeTopic).ConfigureAwait(false);

            var topicMatcher = new TopicMatcher(topic, ns);
            var filterAgents = expectedAgents.Count > 0;

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

                if (filterAgents)
                {
                    var senderId = m.Frame?.Sender?.Identification?.Id ?? string.Empty;
                    if (!expectedAgents.Contains(senderId))
                    {
                        return;
                    }
                }

                Logger.LogDebug("WaitForRegistration: OnMessage received type={Type} sender={Sender}", m.Frame.Type, m.Frame?.Sender?.Identification?.Id);
                queue.Enqueue(m);
            });

            var initialDrained = DrainBufferedMessages(client, topicMatcher, expectedTypeSet, expectedAgents, queue);
            if (initialDrained > 0)
            {
                Logger.LogInformation("WaitForRegistration: drained {Count} buffered registration message(s)", initialDrained);
            }

            var start = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenAgents = new Dictionary<string, I40Message>(StringComparer.OrdinalIgnoreCase);
            var seenCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var firstSeenByAgent = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            while (DateTime.UtcNow - start < timeout)
            {
                var drained = DrainBufferedMessages(client, topicMatcher, expectedTypeSet, expectedAgents, queue);
                if (drained > 0)
                {
                    Logger.LogDebug("WaitForRegistration: drained {Count} buffered registration message(s) during wait loop", drained);
                }

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

                        var agentId = msg.Frame?.Sender?.Identification?.Id ?? seenKey;
                        if (!seenAgents.ContainsKey(agentId))
                        {
                            seenAgents.Add(agentId, msg);
                        }
                        if (!firstSeenByAgent.ContainsKey(agentId))
                        {
                            firstSeenByAgent[agentId] = DateTime.UtcNow;
                        }
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

                    Logger.LogInformation(
                        "WaitForRegistration: received registration from {Id} ({Seen}/{Expected})",
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

                        // Mark that capability extraction/aggregation finished (even if zero capabilities).
                        Context.Set(RegistrationContextKeys.CapabilitiesExtractionComplete, true);

                        var seenDetails = seenAgents.Select(kvp =>
                        {
                            var firstSeen = firstSeenByAgent.TryGetValue(kvp.Key, out var ts)
                                ? ts.ToString("O")
                                : "<unknown>";
                            return $"{kvp.Key}@{firstSeen}";
                        }).ToList();

                        Logger.LogInformation(
                            "WaitForRegistration: expected set reached. Seen agents: {SeenAgents}",
                            string.Join(",", seenDetails));

                        return NodeStatus.Success;
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            if (expectedAgents.Count > 0)
            {
                var missingAgents = expectedAgents.Where(a => !seenAgents.ContainsKey(a)).ToList();
                var seenDetails = FormatSeenDetails(seenAgents, firstSeenByAgent);
                Logger.LogWarning(
                    "WaitForRegistration: timeout waiting on {Topic}. Missing agents: {Missing}. Seen agents: {SeenAgents}",
                    topic,
                    missingAgents.Count > 0 ? string.Join(",", missingAgents) : "<unknown>",
                    seenDetails.Count > 0 ? string.Join(",", seenDetails) : "<none>");
            }
            else
            {
                var seenDetails = FormatSeenDetails(seenAgents, firstSeenByAgent);
                Logger.LogWarning(
                    "WaitForRegistration: timeout waiting for registration on {Topic}. Seen agents: {SeenAgents}",
                    topic,
                    seenDetails.Count > 0 ? string.Join(",", seenDetails) : "<none>");
            }
            return NodeStatus.Failure;
        }

        private int DrainBufferedMessages(
            MessagingClient client,
            TopicMatcher matcher,
            HashSet<string> expectedTypes,
            HashSet<string> expectedAgents,
            ConcurrentQueue<I40Message> queue)
        {
            if (client == null)
            {
                return 0;
            }

            var filterAgents = expectedAgents.Count > 0;
            var matches = client.DequeueMatchingAll((msg, receivedTopic) =>
                matcher.Matches(receivedTopic)
                && msg?.Frame?.Type != null
                && expectedTypes.Contains(msg.Frame.Type)
                && (!filterAgents || expectedAgents.Contains(msg.Frame?.Sender?.Identification?.Id ?? string.Empty)));

            foreach (var (message, matchedTopic) in matches)
            {
                try
                {
                    Logger.LogDebug("WaitForRegistration: buffered match type={Type} topic={Topic} sender={Sender}", message.Frame?.Type, matchedTopic, message.Frame?.Sender?.Identification?.Id);
                }
                catch { }

                queue.Enqueue(message);
            }

            return matches.Count;
        }

        private sealed class TopicMatcher
        {
            private readonly string _moduleTopic;
            private readonly string _namespaceTopic;
            private readonly string _namespacePrefix;

            public TopicMatcher(string configuredTopic, string ns)
            {
                _moduleTopic = NormalizeTopic(configuredTopic);
                _namespacePrefix = BuildNamespacePrefix(configuredTopic, ns);
                _namespaceTopic = string.IsNullOrWhiteSpace(_namespacePrefix)
                    ? string.Empty
                    : _namespacePrefix + "register";
            }

            public bool Matches(string? topic)
            {
                if (string.IsNullOrWhiteSpace(topic))
                {
                    return false;
                }

                var normalized = NormalizeTopic(topic);
                if (string.Equals(normalized, _moduleTopic, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(_namespaceTopic)
                    && string.Equals(normalized, _namespaceTopic, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(_namespacePrefix)
                    && normalized.StartsWith(_namespacePrefix, StringComparison.OrdinalIgnoreCase)
                    && normalized.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }

            private static string NormalizeTopic(string? topic)
            {
                var trimmed = (topic ?? string.Empty).Trim();
                if (!trimmed.StartsWith('/'))
                {
                    trimmed = "/" + trimmed;
                }

                return trimmed.TrimEnd('/');
            }

            private static string BuildNamespacePrefix(string topic, string ns)
            {
                if (!string.IsNullOrWhiteSpace(ns))
                {
                    var sanitized = ns.Trim('/');
                    if (!string.IsNullOrWhiteSpace(sanitized))
                    {
                        return "/" + sanitized + "/";
                    }
                }

                var normalized = NormalizeTopic(topic);
                var parts = normalized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    return "/" + parts[0] + "/";
                }

                return string.Empty;
            }
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

        private static List<string> FormatSeenDetails(Dictionary<string, I40Message> seenAgents, Dictionary<string, DateTime> firstSeenByAgent)
        {
            var list = new List<string>(seenAgents.Count);
            foreach (var kvp in seenAgents)
            {
                var firstSeen = firstSeenByAgent.TryGetValue(kvp.Key, out var ts)
                    ? ts.ToString("O")
                    : "<unknown>";
                list.Add($"{kvp.Key}@{firstSeen}");
            }

            return list;
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

        private HashSet<string> ResolveExpectedAgentsFromPath(string path)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var value = ResolveContextValue(path);
            if (value == null)
            {
                return set;
            }

            var serialized = SerializeContextValue(value);
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return set;
            }

            return ParseExpectedAgents(serialized);
        }

        private object? ResolveContextValue(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim();

            if (Context.Has(trimmed))
            {
                try
                {
                    return Context.Get<object>(trimmed);
                }
                catch
                {
                    return null;
                }
            }

            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            object? root = null;
            var startIndex = 0;

            if (Context.Has(segments[0]))
            {
                try
                {
                    root = Context.Get<object>(segments[0]);
                    startIndex = 1;
                }
                catch
                {
                    root = null;
                }
            }

            if (root == null && Context.Has("config"))
            {
                try
                {
                    root = Context.Get<object>("config");
                    startIndex = string.Equals(segments[0], "config", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                }
                catch
                {
                    root = null;
                }
            }

            if (root == null)
            {
                return null;
            }

            if (startIndex >= segments.Length)
            {
                return root;
            }

            try
            {
                var subPath = segments.Skip(startIndex).ToArray();
                return JsonFacade.GetPath(root, subPath);
            }
            catch
            {
                return null;
            }
        }

        private static string? SerializeContextValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string s)
            {
                return s;
            }

            if (value is IDictionary<string, object?> || value is IList<object?>)
            {
                return JsonFacade.Serialize(value);
            }

            return JsonFacade.ToStringValue(value);
        }
    }
}

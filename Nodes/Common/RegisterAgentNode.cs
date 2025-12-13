using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic node that constructs a `RegisterMessage` and publishes it to the parent register topic.
    /// Behavior depends on `AgentRole` (decides parent):
    /// - Execution/Planning -> parent = ModuleId
    /// - ModuleHolon       -> parent = DispatchingAgent
    /// - DispatchingAgent  -> parent = (namespace) => registers to namespace
    /// If available, reads SubAgents/Inventory/Capabilities from the blackboard context.
    /// </summary>
    public class RegisterAgentNode : BTNode
    {
        // BT-configurable inputs (are set via XML attributes and InterpolateConfigValues)
        public string ParentAgent { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;

        // Consistency with existing MAS-BT topics
        public bool UseLeadingSlash { get; set; } = true;
        public string RegisterTopicSegment { get; set; } = "register";
        public string MessageType { get; set; } = string.Empty;

        public RegisterAgentNode() : base("RegisterAgent") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("RegisterAgent: MessagingClient unavailable");
                return NodeStatus.Failure;
            }

            var ns = !string.IsNullOrWhiteSpace(Namespace)
                ? ResolveTemplates(Namespace)
                : (Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket");

            var role = Context.AgentRole ?? string.Empty;
            var agentId = Context.Get<string>("AgentId")
                         ?? Context.Get<string>("config.Agent.AgentId")
                         ?? Context.AgentId
                         ?? "UnknownAgent";

            // Determine parent agent:
            // 1) explicit node attribute ParentAgent
            // 2) config.Agent.ParentAgent / config.Agent.ParentModuleId
            // 3) role-based fallback
            var parentAgent = ResolveTemplates(ParentAgent);
            if (string.IsNullOrWhiteSpace(parentAgent))
            {
                parentAgent = Context.Get<string>("config.Agent.ParentAgent")
                           ?? Context.Get<string>("config.Agent.ParentId")
                           ?? Context.Get<string>("config.Agent.ParentModuleId")
                           ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(parentAgent))
            {
                if (RoleLooksLikeSubHolon(role))
                {
                    parentAgent = Context.Get<string>("config.Agent.ModuleId")
                               ?? Context.Get<string>("ModuleId")
                               ?? string.Empty;
                }
                else if (role.Contains("ModuleHolon", StringComparison.OrdinalIgnoreCase))
                {
                    parentAgent = "DispatchingAgent";
                }
                else if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
                {
                    parentAgent = string.Empty;
                }
            }

            parentAgent = NormalizeParentAgent(parentAgent, ns);

            // DispatchingAgent: stale modules should be deregistered based on a timeout.
            // This keeps the namespace snapshot (Subagents/Capabilities/InventorySummary) consistent.
            if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var state = Context.Get<DispatchingState>("DispatchingState");
                    if (state != null)
                    {
                        var timeoutSeconds = TryGetInt("config.DispatchingAgent.AgentTimeoutSeconds")
                            ?? TryGetInt("config.DispatchingAgent.ModuleTimeoutSeconds")
                            ?? 30;

                        timeoutSeconds = Math.Max(1, timeoutSeconds);
                        var removed = state.PruneStaleModules(
                            timeout: TimeSpan.FromSeconds(timeoutSeconds),
                            nowUtc: DateTime.UtcNow,
                            excludeModuleId: agentId);

                        if (removed.Count > 0)
                        {
                            Logger.LogInformation(
                                "RegisterAgent: pruned stale modules (timeout={TimeoutSeconds}s): {Modules}",
                                timeoutSeconds,
                                string.Join(", ", removed));
                        }

                        Context.Set("DispatchingState", state);
                    }
                }
                catch
                {
                    // best-effort
                }
            }

            var subagents = ExtractSubAgents();
            var capabilities = ExtractCapabilities();

            var messageType = ResolveTemplates(MessageType);
            if (string.IsNullOrWhiteSpace(messageType) || messageType.Contains('{') || messageType.Contains('}'))
            {
                // Default behavior:
                // - Sub-holons (planning/execution) publish a distinct type so module holons can wait specifically.
                // - ModuleHolon + DispatchingAgent publish "registerMessage" (Dispatching tree waits for this).
                messageType = RoleLooksLikeSubHolon(role)
                    ? "subHolonRegister"
                    : "registerMessage";
            }

            var message = new RegisterMessage(agentId, subagents, capabilities);
            var topic = BuildRegisterTopic(ns, parentAgent);

            try
            {
                var builder = new I40MessageBuilder()
                    .From(agentId, string.IsNullOrWhiteSpace(role) ? "Agent" : role)
                    .To(string.IsNullOrWhiteSpace(parentAgent) ? "Namespace" : parentAgent, null)
                    .WithType(messageType)
                    .WithConversationId(Guid.NewGuid().ToString())
                    .AddElement(message.ToSubmodelElementCollection());

                // DispatchingAgent: publish aggregated inventory summary alongside the RegisterMessage
                if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var state = Context.Get<DispatchingState>("DispatchingState");
                        if (state != null)
                        {
                            var totalFree = 0;
                            var totalOccupied = 0;
                            foreach (var m in state.Modules)
                            {
                                totalFree += m.InventoryFree;
                                totalOccupied += m.InventoryOccupied;
                            }

                            var summary = new SubmodelElementCollection("InventorySummary");
                            var freeProp = new Property("free", new DataType(DataObjectType.Integer));
                            freeProp.Value = new PropertyValue<int>(totalFree);
                            var occupiedProp = new Property("occupied", new DataType(DataObjectType.Integer));
                            occupiedProp.Value = new PropertyValue<int>(totalOccupied);
                            summary.Add(freeProp);
                            summary.Add(occupiedProp);
                            builder.AddElement(summary);
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                }

                await client.PublishAsync(builder.Build(), topic).ConfigureAwait(false);

                Logger.LogDebug("RegisterAgent: sent registration for {AgentId} to {Topic}", agentId, topic);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "RegisterAgent: failed to send registration");
                return NodeStatus.Failure;
            }
        }

        private bool RoleLooksLikeSubHolon(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            return role.Contains("Execution", StringComparison.OrdinalIgnoreCase)
                || role.Contains("Planning", StringComparison.OrdinalIgnoreCase)
                || role.Contains("SubHolon", StringComparison.OrdinalIgnoreCase)
                || role.Contains("PlanningHolon", StringComparison.OrdinalIgnoreCase)
                || role.Contains("ExecutionHolon", StringComparison.OrdinalIgnoreCase);
        }

        private int? TryGetInt(string key)
        {
            if (!Context.Has(key)) return null;

            try
            {
                return Context.Get<int>(key);
            }
            catch
            {
                // ignore
            }

            try
            {
                var s = Context.Get<string>(key);
                if (int.TryParse(s, out var parsed)) return parsed;
            }
            catch
            {
                // ignore
            }

            try
            {
                var elem = Context.Get<JsonElement>(key);
                if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var n))
                {
                    return n;
                }
                if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private string NormalizeParentAgent(string parentAgent, string ns)
        {
            parentAgent = (parentAgent ?? string.Empty).Trim();

            // Still unresolved placeholder (e.g. "{config.Agent.ModuleId}")
            if (parentAgent.Contains('{') || parentAgent.Contains('}'))
            {
                return string.Empty;
            }

            // Some BTs pass namespace as ParentAgent to indicate "register to namespace"
            if (string.Equals(parentAgent, ns, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Normalize DispatchingAgent namespace suffix
            var dispatchingWithSuffix = $"DispatchingAgent_{ns}";
            if (string.Equals(parentAgent, dispatchingWithSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return "DispatchingAgent";
            }

            // Strip accidental leading/trailing slashes
            parentAgent = parentAgent.Trim('/');

            return parentAgent;
        }

        private string ResolveTemplates(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.Contains('{'))
            {
                return value;
            }

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

        private string BuildRegisterTopic(string ns, string parentAgent)
        {
            var prefix = UseLeadingSlash ? "/" : string.Empty;
            var seg = string.IsNullOrWhiteSpace(RegisterTopicSegment) ? "register" : RegisterTopicSegment.Trim('/');

            if (string.IsNullOrWhiteSpace(parentAgent))
            {
                return $"{prefix}{ns}/{seg}";
            }

            return $"{prefix}{ns}/{parentAgent}/{seg}";
        }

        private List<string> ExtractSubAgents()
        {
            // direct keys
            if (Context.Has("SubAgents"))
            {
                var list = Context.Get<List<string>>("SubAgents");
                if (list != null) return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            if (Context.Has("SubHolons"))
            {
                var list = Context.Get<List<string>>("SubHolons");
                if (list != null) return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }

            // DispatchingAgent: infer direct subagents (modules) from registration state
            var role = Context.AgentRole ?? Context.Get<string>("AgentRole") ?? string.Empty;
            if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var state = Context.Get<DispatchingState>("DispatchingState");
                    if (state != null)
                    {
                        var selfId = Context.Get<string>("AgentId")
                                     ?? Context.Get<string>("config.Agent.AgentId")
                                     ?? Context.AgentId
                                     ?? string.Empty;

                        bool LooksLikeInternalSubHolon(string id)
                        {
                            return id.Contains("_Planning", StringComparison.OrdinalIgnoreCase)
                                   || id.Contains("_Execution", StringComparison.OrdinalIgnoreCase);
                        }

                        var result = state.Modules
                            .Select(m => m.ModuleId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => id.Trim())
                            .Where(id => !string.Equals(id, "Namespace", StringComparison.OrdinalIgnoreCase))
                            .Where(id => !string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase))
                            .Where(id => !id.StartsWith("DispatchingAgent", StringComparison.OrdinalIgnoreCase))
                            .Where(id => !LooksLikeInternalSubHolon(id))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (result.Count > 0)
                        {
                            return result;
                        }
                    }
                }
                catch
                {
                    // best-effort
                }
            }

            // config.SubHolons (JsonElement array)
            if (Context.Has("config.SubHolons"))
            {
                try
                {
                    var elem = Context.Get<JsonElement>("config.SubHolons");
                    if (elem.ValueKind == JsonValueKind.Array)
                    {
                        var result = new List<string>();
                        foreach (var e in elem.EnumerateArray())
                        {
                            if (e.ValueKind == JsonValueKind.String)
                            {
                                var v = e.GetString() ?? string.Empty;
                                v = v.Trim();
                                if (v.EndsWith("_agent", StringComparison.OrdinalIgnoreCase))
                                {
                                    v = v.Substring(0, v.Length - "_agent".Length);
                                }
                                if (!string.IsNullOrWhiteSpace(v)) result.Add(v);
                            }
                        }
                        return result;
                    }
                }
                catch { /* ignore */ }
            }

            return new List<string>();
        }

        private List<string> ExtractCapabilities()
        {
            // DispatchingAgent: aggregate all capabilities from registered modules.
            // Duplicates are intentionally allowed.
            var role = Context.AgentRole ?? Context.Get<string>("AgentRole") ?? string.Empty;
            if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var state = Context.Get<DispatchingState>("DispatchingState");
                    if (state != null)
                    {
                        return state.Modules
                            .OrderBy(m => m.ModuleId, StringComparer.OrdinalIgnoreCase)
                            .SelectMany(m => m.Capabilities ?? new List<string>())
                            .Where(c => !string.IsNullOrWhiteSpace(c))
                            .Select(c => c.Trim())
                            .ToList();
                    }
                }
                catch { /* best-effort */ }
            }

            // direct keys
            if (Context.Has("Capabilities"))
            {
                var list = Context.Get<List<string>>("Capabilities");
                if (list != null && list.Count > 0) return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            if (Context.Has("CapabilityNames"))
            {
                var list = Context.Get<List<string>>("CapabilityNames");
                if (list != null && list.Count > 0) return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }

            // config.Agent.Capabilities (JsonElement array)
            if (Context.Has("config.Agent.Capabilities"))
            {
                try
                {
                    var elem = Context.Get<JsonElement>("config.Agent.Capabilities");
                    if (elem.ValueKind == JsonValueKind.Array)
                    {
                        var result = new List<string>();
                        foreach (var e in elem.EnumerateArray())
                        {
                            if (e.ValueKind == JsonValueKind.String)
                            {
                                var v = e.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(v)) result.Add(v);
                            }
                        }
                        return result;
                    }
                }
                catch { /* ignore */ }
            }

            // ModuleHolon: fall back to aggregated capabilities from known sub-holons
            try
            {
                var state = Context.Get<DispatchingState>("DispatchingState");
                if (state != null)
                {
                    var moduleId = ModuleContextHelper.ResolveModuleId(Context);
                    var caps = state.Modules
                        .Where(m => !string.IsNullOrWhiteSpace(m.ModuleId))
                        .Where(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)
                                    || m.ModuleId.StartsWith(moduleId + "_", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(m => m.Capabilities ?? new List<string>())
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (caps.Count > 0)
                    {
                        return caps;
                    }
                }
            }
            catch { /* best-effort */ }

            return new List<string>();
        }

    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using Microsoft.Extensions.Logging;
using MAS_BT.Tools;

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

            // For Planning/Execution sub-holons, ensure the registration identity cannot collide with the ModuleHolon.
            // If the configured AgentId was accidentally overwritten (e.g., by loading a different AAS shell),
            // derive a stable id from ModuleId + Role.
            var registrationAgentId = ResolveRegistrationAgentId(agentId, role);

            // Determine parent agent:
            // 1) explicit node attribute ParentAgent
            // 2) config.Agent.ParentAgent / config.Agent.ParentModuleId (fallback)
            //    Special-case: if config.Agent.ParentAgent equals the namespace (e.g. "_PHUKET"),
            //    treat it as an explicit request to register to /{ns}/register.
            // 3) role-based fallback
            var parentAgent = ResolveTemplates(ParentAgent);
            var configuredParentAgent = Context.Get<string>("config.Agent.ParentAgent")
                                      ?? Context.Get<string>("config.Agent.ParentId")
                                      ?? Context.Get<string>("config.Agent.ParentModuleId")
                                      ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(configuredParentAgent))
            {
                var configParentTrimmed = configuredParentAgent.Trim().Trim('/');
                if (string.Equals(configParentTrimmed, ns, StringComparison.OrdinalIgnoreCase))
                {
                    // Force namespace registration even if the BT configured a different ParentAgent.
                    parentAgent = configuredParentAgent;
                }
                else if (string.IsNullOrWhiteSpace(parentAgent))
                {
                    parentAgent = configuredParentAgent;
                }
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

            var message = new RegisterMessage(registrationAgentId, subagents, capabilities);
            var topic = BuildRegisterTopic(ns, parentAgent);

            try
            {
                var builder = new I40MessageBuilder()
                    .From(registrationAgentId, string.IsNullOrWhiteSpace(role) ? "Agent" : role)
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

                Logger.LogDebug("RegisterAgent: sent registration for {AgentId} to {Topic}", registrationAgentId, topic);
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

        private string ResolveRegistrationAgentId(string currentAgentId, string role)
        {
            if (!RoleLooksLikeSubHolon(role))
            {
                return currentAgentId;
            }

            var moduleId = Context.Get<string>("config.Agent.ModuleId")
                        ?? Context.Get<string>("ModuleId")
                        ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(moduleId))
            {
                // If the current agentId already looks like a sub-holon id for that module, keep it.
                // Example: P102_Planning (configured) should not become P102_Planning_PlanningHolon.
                if (!string.IsNullOrWhiteSpace(currentAgentId)
                    && currentAgentId.StartsWith(moduleId + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return currentAgentId;
                }

                // Otherwise derive a stable id from ModuleId + Role.
                var rolePart = string.IsNullOrWhiteSpace(role) ? "SubHolon" : role.Trim();
                rolePart = rolePart.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
                return $"{moduleId}_{rolePart}";
            }

            return currentAgentId;
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
                var raw = Context.Get<object>(key);
                if (JsonFacade.TryToInt(raw, out var n))
                {
                    return n;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                var s = Context.Get<string>(key);
                if (int.TryParse(s, out var parsed))
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

            // Strip accidental leading/trailing slashes early so comparisons work with "/_PHUKET" etc.
            parentAgent = parentAgent.Trim('/');

            // Some BTs/configs pass namespace as ParentAgent to indicate "register to namespace"
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

            // config.SubHolons (array)
            if (Context.Has("config.SubHolons"))
            {
                try
                {
                    var raw = TryGetConfigValue("config.SubHolons", new[] { "SubHolons" });
                    var result = ExtractStringList(raw, trimSuffixAgent: true);
                    if (result.Count > 0)
                    {
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

            // config.Agent.Capabilities (array)
            if (Context.Has("config.Agent.Capabilities"))
            {
                try
                {
                    var raw = TryGetConfigValue("config.Agent.Capabilities", new[] { "Agent", "Capabilities" });
                    var result = ExtractStringList(raw, trimSuffixAgent: false);
                    if (result.Count > 0)
                    {
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

        private object? TryGetConfigValue(string directKey, IEnumerable<string> path)
        {
            if (Context.Has(directKey))
            {
                try
                {
                    return Context.Get<object>(directKey);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                var configRoot = Context.Get<object>("config");
                return JsonFacade.GetPath(configRoot, path);
            }
            catch
            {
                return null;
            }
        }

        private static List<string> ExtractStringList(object? raw, bool trimSuffixAgent)
        {
            var result = new List<string>();

            if (raw is IEnumerable<string> stringEnumerable)
            {
                foreach (var s in stringEnumerable)
                {
                    AddNormalized(s);
                }

                return result;
            }

            if (raw is IList<object?> list)
            {
                foreach (var item in list)
                {
                    AddNormalized(JsonFacade.ToStringValue(item));
                }

                return result;
            }

            AddNormalized(JsonFacade.ToStringValue(raw));
            return result;

            void AddNormalized(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                var v = value.Trim();
                if (trimSuffixAgent && v.EndsWith("_agent", StringComparison.OrdinalIgnoreCase))
                {
                    v = v.Substring(0, v.Length - "_agent".Length);
                }

                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.Add(v);
                }
            }
        }

    }
}

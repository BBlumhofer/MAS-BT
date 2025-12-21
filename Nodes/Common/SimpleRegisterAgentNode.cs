using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using MAS_BT.Core;
using MAS_BT.Tools;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Lightweight registration node that only relies on the agent's parent, agent id and optionally
    /// found subagents/capabilities/inventory data. It keeps the register message minimal and does
    /// not aggregate dispatcher state.
    /// </summary>
    public class SimpleRegisterAgentNode : BTNode
    {
        public string ParentAgent { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string MessageType { get; set; } = "registerMessage";
        public string RegisterTopicSegment { get; set; } = "register";
        public bool UseLeadingSlash { get; set; } = true;
        public bool WaitForCapabilitiesBeforeRegister { get; set; }
        public int CapabilityExtractionWaitTimeoutMs { get; set; } = 5000;
        public int CapabilityExtractionCheckIntervalMs { get; set; } = 200;

        public SimpleRegisterAgentNode() : base("SimpleRegisterAgent") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("SimpleRegisterAgent: MessagingClient unavailable");
                return NodeStatus.Failure;
            }

            var agentId = ResolveAgentId();
            if (string.IsNullOrWhiteSpace(agentId))
            {
                Logger.LogError("SimpleRegisterAgent: AgentId is required");
                return NodeStatus.Failure;
            }

            var ns = ResolveNamespace();
            var parentAgent = ResolveParentAgent(ns);

            if (WaitForCapabilitiesBeforeRegister)
            {
                await WaitForCapabilitiesExtractionAsync(agentId).ConfigureAwait(false);
            }

            var subagents = ExtractValues("SubAgents", "SubHolons", "config.SubHolons");
            var capabilities = ExtractValues("Capabilities", "CapabilityNames", "config.Agent.Capabilities");
            var messageType = ResolveMessageType();

            var registerMessage = new RegisterMessage(agentId, subagents, capabilities);
            var topic = BuildRegisterTopic(ns, parentAgent);

            var receiverId = string.IsNullOrWhiteSpace(parentAgent) ? ResolveNamespace() : parentAgent;
            var builder = new I40MessageBuilder()
                .From(agentId, Context.AgentRole ?? "Agent")
                .To(receiverId, null)
                .WithType(messageType)
                .WithConversationId(Guid.NewGuid().ToString())
                .AddElement(registerMessage.ToSubmodelElementCollection());

            TryAttachInventorySummary(builder);

            try
            {
                await client.PublishAsync(builder.Build(), topic).ConfigureAwait(false);
                Logger.LogDebug("SimpleRegisterAgent: published {AgentId} to {Topic}", agentId, topic);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SimpleRegisterAgent: failed to publish registration");
                return NodeStatus.Failure;
            }
        }

        private string ResolveAgentId()
        {
            var candidate = Context.AgentId
                            ?? Context.Get<string>("AgentId")
                            ?? Context.Get<string>("config.Agent.AgentId")
                            ?? Context.Get<string>("config.Agent.Id")
                            ?? string.Empty;
            return ResolveTemplates(candidate).Trim();
        }

        private string ResolveParentAgent(string ns)
        {
            var candidate = ResolveTemplates(ParentAgent);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = Context.Get<string>("config.Agent.ParentAgent") ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            candidate = NormalizeParentAgent(candidate, ns);
            if (string.Equals(candidate, ns, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return candidate;
        }

        private string ResolveNamespace()
        {
            var candidate = ResolveTemplates(Namespace);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = Context.Get<string>("config.Namespace")
                            ?? Context.Get<string>("Namespace")
                            ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(candidate) ? "default" : candidate.Trim();
        }

        private string ResolveMessageType()
        {
            var candidate = ResolveTemplates(MessageType);
            return string.IsNullOrWhiteSpace(candidate) ? "registerMessage" : candidate;
        }

        private List<string> ExtractValues(params string[] keys)
        {
            var result = new List<string>();
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (Context.Has(key))
                {
                    var raw = Context.Get<object>(key);
                    result.AddRange(NormalizeStrings(raw));
                }
                else if (key.StartsWith("config.", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = key.Substring("config.".Length).Split('.', StringSplitOptions.RemoveEmptyEntries);
                    var raw = TryGetConfigValue(key, segments);
                    result.AddRange(NormalizeStrings(raw));
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IEnumerable<string> NormalizeStrings(object? raw)
        {
            if (raw == null)
            {
                return Enumerable.Empty<string>();
            }

            if (raw is IEnumerable<string> stringEnumerable)
            {
                return stringEnumerable
                    .Select(s => s?.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!);
            }

            if (raw is IEnumerable<object> objects)
            {
                return objects
                    .Select(JsonFacade.ToStringValue)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim());
            }

            var single = JsonFacade.ToStringValue(raw);
            if (string.IsNullOrWhiteSpace(single))
            {
                return Enumerable.Empty<string>();
            }

            return new[] { single.Trim() };
        }

        private object? TryGetConfigValue(string key, IEnumerable<string> path)
        {
            if (Context.Has(key))
            {
                try
                {
                    return Context.Get<object>(key);
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

        private string NormalizeParentAgent(string parent, string ns)
        {
            parent = parent.Trim().Trim('/');
            if (string.Equals(parent, $"DispatchingAgent_{ns}", StringComparison.OrdinalIgnoreCase))
            {
                return "DispatchingAgent";
            }

            return parent;
        }

        private void TryAttachInventorySummary(I40MessageBuilder builder)
        {
            try
            {
                if (Context.Get<object>("InventorySummary") is SubmodelElementCollection summary)
                {
                    builder.AddElement(summary);
                }
            }
            catch
            {
                // ignore
            }
        }

        private string ResolveTemplates(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Contains('{'))
            {
                return value;
            }

            var result = value;
            var index = 0;
            while (true)
            {
                var open = result.IndexOf('{', index);
                if (open < 0) break;
                var close = result.IndexOf('}', open + 1);
                if (close <= open) break;

                var token = result.Substring(open + 1, close - open - 1);
                var replacement = Context.Get<string>(token);
                if (string.IsNullOrWhiteSpace(replacement))
                {
                    replacement = Context.Get<string>($"config.{token}");
                }

                if (!string.IsNullOrWhiteSpace(replacement))
                {
                    result = result.Substring(0, open) + replacement + result.Substring(close + 1);
                    index = open + replacement.Length;
                    continue;
                }

                index = close + 1;
            }

            return result;
        }

        private bool IsNamespaceAgent()
        {
            var role = (Context.AgentRole ?? Context.Get<string>("AgentRole") ?? string.Empty)
                .Trim();
            return role.Contains("Namespace", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().Trim('/');
        }

        private static List<string> SplitSegments(string value)
        {
            return value
                .Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSegment)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        private string BuildRegisterTopic(string ns, string parent)
        {
            var prefix = UseLeadingSlash ? "/" : string.Empty;
            var segment = string.IsNullOrWhiteSpace(RegisterTopicSegment) ? "register" : RegisterTopicSegment.Trim('/');

            var ancestors = new List<string>();
            var normalizedParent = NormalizeSegment(parent);
            if (!string.IsNullOrWhiteSpace(normalizedParent))
            {
                ancestors.AddRange(SplitSegments(normalizedParent));
            }

            var normalizedNamespace = NormalizeSegment(ns);
            if (!IsNamespaceAgent() && !string.IsNullOrWhiteSpace(normalizedNamespace))
            {
                var nsAlready = ancestors.Any(s => string.Equals(s, normalizedNamespace, StringComparison.OrdinalIgnoreCase));
                if (!nsAlready)
                {
                    ancestors.Insert(0, normalizedNamespace);
                }
            }

            ancestors = ancestors
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (ancestors.Count == 0)
            {
                return $"{prefix}{segment}";
            }

            return $"{prefix}{string.Join("/", ancestors)}/{segment}";
        }

        private async Task WaitForCapabilitiesExtractionAsync(string agentId)
        {
            if (IsCapabilitiesExtractionComplete())
            {
                return;
            }

            var timeoutMs = Math.Max(0, CapabilityExtractionWaitTimeoutMs);
            var intervalMs = Math.Max(20, CapabilityExtractionCheckIntervalMs);
            var timeoutDescription = timeoutMs <= 0 ? "indefinitely" : $"{timeoutMs}ms";

            Logger.LogInformation("SimpleRegisterAgent: waiting {Timeout} for capability extraction before registering {AgentId}",
                timeoutDescription, agentId);

            var elapsedMs = 0;
            while (!IsCapabilitiesExtractionComplete() && (timeoutMs <= 0 || elapsedMs < timeoutMs))
            {
                await Task.Delay(intervalMs).ConfigureAwait(false);
                elapsedMs += intervalMs;
            }

            if (!IsCapabilitiesExtractionComplete())
            {
                var reportedWait = timeoutMs <= 0 ? elapsedMs : Math.Min(elapsedMs, timeoutMs);
                Logger.LogWarning("SimpleRegisterAgent: capability extraction still pending after {Waited}ms for {AgentId}; continuing anyway.",
                    reportedWait, agentId);
            }
        }

        private bool IsCapabilitiesExtractionComplete()
        {
            if (Context.Has(RegistrationContextKeys.CapabilitiesExtractionComplete))
            {
                try
                {
                    return Context.Get<bool>(RegistrationContextKeys.CapabilitiesExtractionComplete);
                }
                catch
                {
                    // best effort
                }
            }

            if (Context.Has("Capabilities") || Context.Has("CapabilityNames"))
            {
                return true;
            }

            return false;
        }
    }
}

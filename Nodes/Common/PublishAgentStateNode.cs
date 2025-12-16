using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic publish node.
    /// - For dispatching roles: publishes DispatchingState snapshot to /{ns}/DispatchingAgent/logs.
    /// - For all other roles: no-op (Success).
    ///
    /// This node publishes once per tick invocation and returns Success (BT-friendly).
    /// </summary>
    public class PublishAgentStateNode : BTNode
    {
        public string Role { get; set; } = string.Empty;
        public int PublishIntervalSeconds { get; set; } = 30;

        private string _lastSnapshot = string.Empty;
        private DateTime _lastPublishUtc = DateTime.MinValue;

        public PublishAgentStateNode() : base("PublishAgentState") { }

        public override async Task<NodeStatus> Execute()
        {
            var role = !string.IsNullOrWhiteSpace(Role) ? ResolveTemplates(Role) : (Context.AgentRole ?? string.Empty);
            if (!role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                return NodeStatus.Success;
            }

            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogWarning("PublishAgentState: MessagingClient unavailable or disconnected");
                return NodeStatus.Failure;
            }

            var state = Context.Get<DispatchingState>("DispatchingState");
            if (state == null)
            {
                Logger.LogWarning("PublishAgentState: DispatchingState not set");
                return NodeStatus.Failure;
            }

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var topic = $"/{ns}/DispatchingAgent/logs";

            var snapshot = state.Modules
                .Where(m => m != null)
                .Select(m => new
                {
                    ModuleId = m.ModuleId ?? string.Empty,
                    Capabilities = (m.Capabilities ?? new List<string>())
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                })
                .Where(m => !string.IsNullOrWhiteSpace(m.ModuleId))
                .OrderBy(m => m.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var serialized = JsonSerializer.Serialize(snapshot);
            var changed = !string.Equals(serialized, _lastSnapshot, StringComparison.Ordinal);
            var timeSinceLast = (DateTime.UtcNow - _lastPublishUtc).TotalSeconds;

            if (!changed && timeSinceLast < PublishIntervalSeconds)
            {
                return NodeStatus.Success;
            }

            var builder = new I40MessageBuilder()
                .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                .To("Broadcast", "System")
                .WithType("dispatchingStateLog")
                .WithConversationId(Guid.NewGuid().ToString())
                .AddElement(new Property<string>("DispatchingState")
                {
                    Value = new PropertyValue<string>(serialized)
                });

            try
            {
                await client.PublishAsync(builder.Build(), topic).ConfigureAwait(false);
                _lastSnapshot = serialized;
                _lastPublishUtc = DateTime.UtcNow;
                Logger.LogInformation("PublishAgentState: published dispatching state to {Topic} (changed={Changed})", topic, changed);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "PublishAgentState: failed to publish state");
                return NodeStatus.Failure;
            }
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
    }
}

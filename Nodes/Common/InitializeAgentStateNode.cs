using System;
using System.Text.Json;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic initialization node.
    /// - For dispatching roles: ensures DispatchingState exists and seeds it from config.DispatchingAgent.Modules.
    /// - For all other roles: ensures DispatchingState exists (no seeding).
    /// </summary>
    public class InitializeAgentStateNode : BTNode
    {
        public string Role { get; set; } = string.Empty;

        public InitializeAgentStateNode() : base("InitializeAgentState") { }

        public override Task<NodeStatus> Execute()
        {
            var role = !string.IsNullOrWhiteSpace(Role) ? ResolveTemplates(Role) : (Context.AgentRole ?? string.Empty);

            var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
            Context.Set("DispatchingState", state);

            if (!role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(NodeStatus.Success);
            }

            try
            {
                var modulesElement = Context.Get<JsonElement>("config.DispatchingAgent.Modules");
                if (modulesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var moduleElem in modulesElement.EnumerateArray())
                    {
                        var module = ParseModule(moduleElem);
                        if (module != null)
                        {
                            state.Upsert(module);
                        }
                    }
                }

                Logger.LogInformation("InitializeAgentState: loaded {Count} modules from config", state.Modules.Count);
                return Task.FromResult(NodeStatus.Success);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "InitializeAgentState: failed to load modules from config");
                return Task.FromResult(NodeStatus.Failure);
            }
        }

        private DispatchingModuleInfo? ParseModule(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var moduleId = element.TryGetProperty("ModuleId", out var idElem) && idElem.ValueKind == JsonValueKind.String
                ? idElem.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                return null;
            }

            var info = new DispatchingModuleInfo
            {
                ModuleId = moduleId!,
                AasId = element.TryGetProperty("AasId", out var aasElem) && aasElem.ValueKind == JsonValueKind.String
                    ? aasElem.GetString()
                    : null,
                LastRegistrationUtc = DateTime.UtcNow
            };

            if (element.TryGetProperty("Capabilities", out var capsElem) && capsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var cap in capsElem.EnumerateArray())
                {
                    if (cap.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cap.GetString()))
                    {
                        info.Capabilities.Add(cap.GetString()!);
                    }
                }
            }

            if (element.TryGetProperty("Neighbors", out var neighElem) && neighElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var neighbor in neighElem.EnumerateArray())
                {
                    if (neighbor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(neighbor.GetString()))
                    {
                        info.Neighbors.Add(neighbor.GetString()!);
                    }
                }
            }

            return info;
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

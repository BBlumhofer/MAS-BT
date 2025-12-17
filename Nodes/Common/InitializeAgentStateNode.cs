using System;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using MAS_BT.Tools;

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
                var rawModules = TryGetConfigValue("config.DispatchingAgent.Modules", new[] { "DispatchingAgent", "Modules" });
                if (rawModules is IList<object?> modules)
                {
                    foreach (var moduleElem in modules)
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

        private DispatchingModuleInfo? ParseModule(object? element)
        {
            if (element is not IDictionary<string, object?> dict)
            {
                return null;
            }

            var moduleId = dict.TryGetValue("ModuleId", out var idRaw)
                ? JsonFacade.ToStringValue(idRaw)
                : null;

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                return null;
            }

            var info = new DispatchingModuleInfo
            {
                ModuleId = moduleId!,
                AasId = dict.TryGetValue("AasId", out var aasRaw)
                    ? JsonFacade.ToStringValue(aasRaw)
                    : null,
                LastRegistrationUtc = DateTime.UtcNow
            };

            if (dict.TryGetValue("Capabilities", out var capsRaw) && capsRaw is IList<object?> caps)
            {
                foreach (var cap in caps)
                {
                    var capStr = JsonFacade.ToStringValue(cap);
                    if (!string.IsNullOrWhiteSpace(capStr))
                    {
                        info.Capabilities.Add(capStr!);
                    }
                }
            }

            if (dict.TryGetValue("Neighbors", out var neighRaw) && neighRaw is IList<object?> neighbors)
            {
                foreach (var neighbor in neighbors)
                {
                    var neighborStr = JsonFacade.ToStringValue(neighbor);
                    if (!string.IsNullOrWhiteSpace(neighborStr))
                    {
                        info.Neighbors.Add(neighborStr!);
                    }
                }
            }

            return info;
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

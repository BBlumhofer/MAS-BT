using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Services;
using MAS_BT.Tools;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Orchestration;

/// <summary>
/// NamesapceHolon-specific node that spawns all configured sub-holons.
/// Ensures deterministic ordering and logs the spawn attempts.
/// </summary>
public class SpawnNamespaceSubHolonsNode : BTNode
{
    public SpawnNamespaceSubHolonsNode() : base("SpawnNamespaceSubHolons") { }

    public override async Task<NodeStatus> Execute()
    {
        var config = Context.Get<object>("config");
        if (config is null)
        {
            Logger.LogWarning("SpawnNamespaceSubHolons: no config found");
            return NodeStatus.Success;
        }

        try
        {
            var entries = ExtractSubHolonEntries(config).ToList();
            if (entries.Count == 0)
            {
                Logger.LogInformation("SpawnNamespaceSubHolons: no SubHolons entries found; skipping");
                return NodeStatus.Success;
            }

            var parentConfigPath = Context.Get<string>("config.Path");
            var baseDir = string.IsNullOrWhiteSpace(parentConfigPath)
                ? (Context.Get<string>("config.Directory") ?? string.Empty)
                : (Path.GetDirectoryName(parentConfigPath) ?? string.Empty);

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                Logger.LogWarning("SpawnNamespaceSubHolons: parent config path/directory missing; cannot resolve sub-holons");
                return NodeStatus.Failure;
            }

            var useTerminal = Context.Get<bool?>("SpawnSubHolonsInTerminal") ?? false;
            var launcher = Context.Get<ISubHolonLauncher>("SubHolonLauncher")
                           ?? new ProcessSubHolonLauncher(Logger, useTerminal);

            var ns = Context.Get<string>("Namespace") ?? Context.Get<string>("config.Namespace") ?? string.Empty;
            var launchTasks = new List<Task>();
            foreach (var entry in entries)
            {
                if (!TryResolveSubHolon(entry, baseDir, out var configPath, out var treePath, out var agentId))
                {
                    Logger.LogWarning("SpawnNamespaceSubHolons: skipping entry {Entry} (missing config or InitializationTree)", entry);
                    continue;
                }

                Logger.LogInformation(
                    "SpawnNamespaceSubHolons: launching {AgentId} with tree {TreePath} (namespace {Namespace})",
                    agentId,
                    treePath,
                    ns);

                var spec = new SubHolonLaunchSpec(treePath, configPath, ns, agentId);
                launchTasks.Add(launcher.LaunchAsync(spec));
            }

            await Task.WhenAll(launchTasks);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SpawnNamespaceSubHolons: failed to launch sub-holons");
            return NodeStatus.Failure;
        }
    }

    private static IEnumerable<string> ExtractSubHolonEntries(object? config)
    {
        var subHolons = JsonFacade.GetPath(config, new[] { "SubHolons" });
        if (subHolons is IList<object?> arr)
        {
            foreach (var entry in arr)
            {
                var s = entry as string ?? JsonFacade.ToStringValue(entry);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    yield return s;
                }
            }
        }
    }

    private bool TryResolveSubHolon(string entry, string baseDir, out string configPath, out string? treePath, out string? agentId)
    {
        configPath = string.Empty;
        treePath = null;
        agentId = null;

        var candidate = entry;
        if (!candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            candidate += ".json";
        }

        var possiblePath = Path.IsPathRooted(candidate) ? candidate : Path.Combine(baseDir, candidate);
        if (!File.Exists(possiblePath))
        {
            return false;
        }

        configPath = Path.GetFullPath(possiblePath);

        try
        {
            var root = JsonFacade.ParseFile(configPath);
            treePath = JsonFacade.GetPathAsString(root, new[] { "InitializationTree" })
                       ?? JsonFacade.GetPathAsString(root, new[] { "Agent", "InitializationTree" });
            agentId = JsonFacade.GetPathAsString(root, new[] { "Agent", "AgentId" });

            if (string.IsNullOrWhiteSpace(treePath))
            {
                return false;
            }

            if (!Path.IsPathRooted(treePath))
            {
                var combined = Path.Combine(Path.GetDirectoryName(configPath) ?? baseDir, treePath);
                if (File.Exists(combined))
                {
                    treePath = Path.GetFullPath(combined);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SpawnNamespaceSubHolons: failed to parse child config {Config}", configPath);
            return false;
        }

        return !string.IsNullOrWhiteSpace(treePath);
    }
}

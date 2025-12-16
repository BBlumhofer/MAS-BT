using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using MAS_BT.Core;
using MAS_BT.Services;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.ModuleHolon;

public class SpawnSubHolonsNode : BTNode
{
    public SpawnSubHolonsNode() : base("SpawnSubHolons") {}

    public override async Task<NodeStatus> Execute()
    {
        var config = Context.Get<JsonElement>("config");
        if (config.ValueKind == JsonValueKind.Undefined)
        {
            Logger.LogWarning("SpawnSubHolons: no config found");
            return NodeStatus.Success;
        }

        try
        {
            var subEntries = ExtractSubHolonEntries(config).ToList();
            if (subEntries.Count == 0)
            {
                Logger.LogInformation("SpawnSubHolons: no SubHolons entries found; skipping");
                return NodeStatus.Success;
            }

            var moduleId = ModuleContextHelper.ResolveModuleId(Context);
            var parentConfigPath = Context.Get<string>("config.Path");
            var baseDir = string.IsNullOrWhiteSpace(parentConfigPath)
                ? (Context.Get<string>("config.Directory") ?? string.Empty)
                : (Path.GetDirectoryName(parentConfigPath) ?? string.Empty);

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                Logger.LogWarning("SpawnSubHolons: parent config path/directory missing; cannot resolve sub-holons");
                return NodeStatus.Failure;
            }

            var useTerminal = Context.Get<bool?>("SpawnSubHolonsInTerminal") ?? false;
            var launcher = Context.Get<ISubHolonLauncher>("SubHolonLauncher")
                           ?? new ProcessSubHolonLauncher(Logger, useTerminal);

            var launchTasks = new List<Task>();
            foreach (var entry in subEntries)
            {
                if (!TryResolveSubHolon(entry, baseDir, out var configPath, out var treePath, out var agentId))
                {
                    Logger.LogWarning("SpawnSubHolons: skipping sub-holon entry {Entry} (missing config or InitializationTree)", entry);
                    continue;
                }

                var spec = new SubHolonLaunchSpec(
                    treePath,
                    configPath,
                    moduleId,
                    agentId ?? BuildAgentId(moduleId, entry));

                launchTasks.Add(launcher.LaunchAsync(spec));
            }

            await Task.WhenAll(launchTasks);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SpawnSubHolons: failed to spawn sub-holons");
            return NodeStatus.Failure;
        }
    }

    private static IEnumerable<string> ExtractSubHolonEntries(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            yield break;

        if (config.TryGetProperty("SubHolons", out var subHolons) && subHolons.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in subHolons.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                {
                    yield return entry.GetString()!;
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
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;

            treePath = TryGetString(root, "InitializationTree")
                       ?? TryGetString(root, "Agent", "InitializationTree");
            agentId = TryGetString(root, "Agent", "AgentId");

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
            Logger.LogWarning(ex, "SpawnSubHolons: failed to parse sub-holon config {Config}", configPath);
            return false;
        }

        return !string.IsNullOrWhiteSpace(treePath);
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string BuildAgentId(string moduleId, string treePath)
    {
        var suffix = Path.GetFileNameWithoutExtension(treePath);
        return string.IsNullOrWhiteSpace(suffix)
            ? moduleId
            : $"{moduleId}_{suffix}";
    }
}

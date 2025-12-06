using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// MonitoringSkill - Liest Skill-Status und Monitoring-Variablen für geschlossene Regelkreise
/// </summary>
public class MonitoringSkillNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;

    public MonitoringSkillNode() : base("MonitoringSkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        // Resolve runtime placeholders from Blackboard
        var resolvedModuleName = ResolveBlackboardPlaceholder(ModuleName);
        var resolvedSkillName = ResolveBlackboardPlaceholder(SkillName);

        Logger.LogInformation("MonitoringSkill: Reading state and monitoring data for {SkillName} in module {ModuleName}", 
            resolvedSkillName, resolvedModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("MonitoringSkill: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(resolvedModuleName, out var module))
            {
                Logger.LogError("MonitoringSkill: Module {ModuleName} not found", resolvedModuleName);
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(resolvedSkillName, out var skill))
            {
                Logger.LogError("MonitoringSkill: Skill {SkillName} not found", resolvedSkillName);
                return NodeStatus.Failure;
            }

            // Lese aktuellen Skill-Status
            var currentState = skill.CurrentState;
            Logger.LogDebug("MonitoringSkill: Current skill state: {State}", currentState);
            Set($"skill_{resolvedSkillName}_state", currentState.ToString());

            // TODO: Lese MonitoringData Variablen wenn API verfügbar
            // var monitoringData = await skill.GetMonitoringDataAsync();
            // Set($"skill_{SkillName}_monitoring", monitoringData);

            // Für jetzt: Speichere nur den Status
            var monitoringDict = new Dictionary<string, object>
            {
                { "CurrentState", currentState.ToString() },
                { "UpdatedAt", DateTime.UtcNow }
            };

            Set($"skill_{resolvedSkillName}_monitoring", monitoringDict);

            Logger.LogInformation("MonitoringSkill: Successfully read monitoring data for {SkillName}", resolvedSkillName);
            await Task.CompletedTask;
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MonitoringSkill: Error reading monitoring data");
            return NodeStatus.Failure;
        }
    }

    /// <summary>
    /// Löst Blackboard-Platzhalter zur Laufzeit auf (z.B. {MachineName} → "ScrewingStation")
    /// </summary>
    private string ResolveBlackboardPlaceholder(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("{") || !value.EndsWith("}"))
            return value;

        var key = value.Substring(1, value.Length - 2); // Entferne { }
        var resolvedValue = Context.Get<string>(key);

        if (resolvedValue != null)
        {
            Logger.LogDebug("Resolved placeholder {{{Key}}} → {Value}", key, resolvedValue);
            return resolvedValue;
        }

        Logger.LogWarning("Could not resolve placeholder {{{Key}}}, using literal value", key);
        return value;
    }
}

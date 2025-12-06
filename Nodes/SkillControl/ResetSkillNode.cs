using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// ResetSkill - Setzt einen Skill im Halted oder Completed State zurück zu Ready
/// </summary>
public class ResetSkillNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;

    public ResetSkillNode() : base("ResetSkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        // Resolve Placeholders zur Laufzeit
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        var resolvedSkillName = ResolvePlaceholders(SkillName);
        
        Logger.LogInformation("ResetSkill: Resetting skill {SkillName} in module {ModuleName}", 
            resolvedSkillName, resolvedModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("ResetSkill: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(resolvedModuleName, out var module))
            {
                Logger.LogError("ResetSkill: Module {ModuleName} not found", resolvedModuleName);
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(resolvedSkillName, out var skill))
            {
                Logger.LogError("ResetSkill: Skill {SkillName} not found", resolvedSkillName);
                return NodeStatus.Failure;
            }

            var currentState = skill.CurrentState;
            Logger.LogDebug("ResetSkill: Current state of {SkillName}: {State}", resolvedSkillName, currentState);

            // Prüfe ob Reset nötig ist
            if (currentState == SkillStates.Ready)
            {
                Logger.LogInformation("ResetSkill: Skill {SkillName} is already in Ready state", resolvedSkillName);
                return NodeStatus.Success;
            }

            // Reset nur möglich von Halted oder Completed
            if (currentState != SkillStates.Halted && currentState != SkillStates.Completed)
            {
                Logger.LogWarning("ResetSkill: Cannot reset skill {SkillName} from state {State}. Expected Halted or Completed.", 
                    resolvedSkillName, currentState);
                return NodeStatus.Failure;
            }

            // Führe Reset aus
            await skill.ResetAsync();
            Logger.LogDebug("ResetSkill: ResetAsync called for {SkillName}", resolvedSkillName);

            // Warte auf Ready state
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                var newState = skill.CurrentState;
                
                if (newState == SkillStates.Ready)
                {
                    Logger.LogInformation("ResetSkill: Skill {SkillName} successfully reset to Ready state", resolvedSkillName);
                    Set($"skill_{resolvedSkillName}_state", SkillStates.Ready.ToString());
                    return NodeStatus.Success;
                }

                // Prüfe auf Error während Reset
                if (newState == SkillStates.Halted && currentState != SkillStates.Halted)
                {
                    Logger.LogError("ResetSkill: Skill {SkillName} entered Halted state during reset", resolvedSkillName);
                    Set($"skill_{resolvedSkillName}_state", SkillStates.Halted.ToString());
                    return NodeStatus.Failure;
                }

                await Task.Delay(200);
            }

            // Timeout
            var finalState = skill.CurrentState;
            Logger.LogError("ResetSkill: Timeout waiting for Ready state. Current state: {State}", finalState);
            Set($"skill_{resolvedSkillName}_state", finalState.ToString());
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ResetSkill: Error resetting skill {SkillName}", resolvedSkillName);
            return NodeStatus.Failure;
        }
    }
}

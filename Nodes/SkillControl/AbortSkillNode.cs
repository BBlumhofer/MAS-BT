using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// AbortSkill - Bricht einen laufenden Skill ab und wartet auf Halted state
/// </summary>
public class AbortSkillNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public bool WaitForHalted { get; set; } = true;

    public AbortSkillNode() : base("AbortSkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("AbortSkill: Aborting skill {SkillName} in module {ModuleName}", 
            SkillName, ModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("AbortSkill: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("AbortSkill: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(SkillName, out var skill))
            {
                Logger.LogError("AbortSkill: Skill {SkillName} not found", SkillName);
                return NodeStatus.Failure;
            }

            // Markiere Abort als explizit angefordert (verhindert false error detection)
            Set($"skill_{SkillName}_abort_requested", true);

            // Rufe HaltAsync auf (via dynamic call oder Interface)
            try
            {
                if (skill is IAsyncCallable callable)
                {
                    await callable.CallAsync("HaltAsync");
                    Logger.LogDebug("AbortSkill: HaltAsync called for {SkillName}", SkillName);
                }
                else
                {
                    Logger.LogWarning("AbortSkill: Skill does not support async calls");
                    return NodeStatus.Failure;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "AbortSkill: Error calling HaltAsync");
                return NodeStatus.Failure;
            }

            // Warte auf Halted state wenn gewünscht
            if (WaitForHalted)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < TimeoutSeconds)
                {
                    if (skill.CurrentState == UAClient.Common.SkillStates.Halted)
                    {
                        Logger.LogInformation("AbortSkill: Skill {SkillName} reached Halted state", SkillName);
                        Set($"skill_{SkillName}_state", UAClient.Common.SkillStates.Halted.ToString());
                        return NodeStatus.Success;
                    }

                    await Task.Delay(500);
                }

                Logger.LogError("AbortSkill: Timeout waiting for Halted state");
                Set($"skill_{SkillName}_state", skill.CurrentState.ToString());
                return NodeStatus.Failure;
            }

            Set($"skill_{SkillName}_state", skill.CurrentState.ToString());
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AbortSkill: Error aborting skill");
            return NodeStatus.Failure;
        }
    }
}


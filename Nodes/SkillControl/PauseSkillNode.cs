using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// PauseSkill - Pausiert einen laufenden Skill (Transition zu Suspended state)
/// </summary>
public class PauseSkillNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public PauseSkillNode() : base("PauseSkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("PauseSkill: Pausing skill {SkillName} in module {ModuleName}", 
            SkillName, ModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("PauseSkill: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("PauseSkill: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(SkillName, out var skill))
            {
                Logger.LogError("PauseSkill: Skill {SkillName} not found", SkillName);
                return NodeStatus.Failure;
            }

            // TODO: Skill Pause/Suspend functionality not yet implemented in RemoteSkill
            // RemoteSkill hat aktuell keine SuspendAsync Methode
            // Dies erfordert OPC UA Command Call oder erweiterte RemoteSkill API
            Logger.LogWarning("PauseSkill: Suspend functionality not yet implemented in RemoteSkill API");
            Logger.LogInformation("PauseSkill: Current skill state: {State}", skill.CurrentState);
            
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "PauseSkill: Error pausing skill");
            return NodeStatus.Failure;
        }
    }
}


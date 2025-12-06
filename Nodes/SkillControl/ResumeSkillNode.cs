using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// ResumeSkill - Setzt einen pausierten Skill fort (Transition von Suspended zu Running)
/// </summary>
public class ResumeSkillNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public ResumeSkillNode() : base("ResumeSkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("ResumeSkill: Resuming skill {SkillName} in module {ModuleName}", 
            SkillName, ModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("ResumeSkill: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("ResumeSkill: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(SkillName, out var skill))
            {
                Logger.LogError("ResumeSkill: Skill {SkillName} not found", SkillName);
                return NodeStatus.Failure;
            }

            // TODO: Skill Resume/Unsuspend functionality not yet implemented in RemoteSkill
            // RemoteSkill hat aktuell keine UnsuspendAsync Methode
            // Dies erfordert OPC UA Command Call oder erweiterte RemoteSkill API
            Logger.LogWarning("ResumeSkill: Unsuspend functionality not yet implemented in RemoteSkill API");
            Logger.LogInformation("ResumeSkill: Current skill state: {State}", skill.CurrentState);
            
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ResumeSkill: Error resuming skill");
            return NodeStatus.Failure;
        }
    }
}


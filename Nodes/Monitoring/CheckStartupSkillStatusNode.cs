using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.Monitoring;

/// <summary>
/// CheckStartupSkillStatus - Prüft ob der StartupSkill läuft
/// Einfache fokussierte Node für Skill-Status-Checks
/// </summary>
public class CheckStartupSkillStatusNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;

    public CheckStartupSkillStatusNode() : base("CheckStartupSkillStatus")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("CheckStartupSkillStatus: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            // Finde Modul
            RemoteModule? module = null;
            if (!string.IsNullOrEmpty(ModuleName))
            {
                if (!server.Modules.TryGetValue(ModuleName, out module))
                {
                    Logger.LogError("CheckStartupSkillStatus: Module {ModuleName} not found", ModuleName);
                    return NodeStatus.Failure;
                }
            }
            else
            {
                module = server.Modules.Values.FirstOrDefault(m => m.SkillSet.Count > 0);
                if (module == null)
                {
                    Logger.LogError("CheckStartupSkillStatus: No modules available");
                    return NodeStatus.Failure;
                }
            }

            // Finde StartupSkill
            var startupSkill = module.SkillSet.Values
                .FirstOrDefault(s => s.Name.IndexOf("Startup", StringComparison.OrdinalIgnoreCase) >= 0);

            if (startupSkill == null)
            {
                Logger.LogWarning("CheckStartupSkillStatus: No StartupSkill found in module {ModuleName}", module.Name);
                Set("startupSkillRunning", false);
                return NodeStatus.Failure;
            }

            // Prüfe Skill-Status
            var state = await startupSkill.GetStateAsync();
            
            if (state.HasValue && state.Value == (int)UAClient.Common.SkillStates.Running)
            {
                Logger.LogDebug("CheckStartupSkillStatus: StartupSkill is running");
                Set("startupSkillRunning", true);
                Set("startupSkillState", "Running");
                return NodeStatus.Success;
            }
            else
            {
                var stateName = state.HasValue ? ((SkillStates)state.Value).ToString() : "Unknown";
                Logger.LogWarning("CheckStartupSkillStatus: StartupSkill is NOT running (State: {State})", stateName);
                Set("startupSkillRunning", false);
                Set("startupSkillState", stateName);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CheckStartupSkillStatus: Error checking startup skill status");
            Set("startupSkillRunning", false);
            return NodeStatus.Failure;
        }
    }
}

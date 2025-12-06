using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.Recovery;

/// <summary>
/// HaltAllSkills - Haltet alle laufenden Skills im Modul
/// Wird bei Recovery verwendet um einen sauberen Zustand herzustellen
/// </summary>
public class HaltAllSkillsNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public HaltAllSkillsNode() : base("HaltAllSkills")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("HaltAllSkills: Halting all skills in module {ModuleName}", ModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("HaltAllSkills: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("HaltAllSkills: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            var skills = module.SkillSet.Values.ToList();
            if (skills.Count == 0)
            {
                Logger.LogInformation("HaltAllSkills: No skills found in module");
                return NodeStatus.Success;
            }

            Logger.LogInformation("HaltAllSkills: Found {Count} skills to halt", skills.Count);

            // Sende HaltAsync an alle Skills die nicht bereits Halted sind
            var skillsToHalt = skills.Where(s => s.CurrentState != SkillStates.Halted).ToList();
            
            if (skillsToHalt.Count == 0)
            {
                Logger.LogInformation("HaltAllSkills: All skills already Halted");
                return NodeStatus.Success;
            }

            Logger.LogInformation("HaltAllSkills: Halting {Count} skills: {Skills}", 
                skillsToHalt.Count, 
                string.Join(", ", skillsToHalt.Select(s => s.Name)));

            // Halt alle Skills parallel
            foreach (var skill in skillsToHalt)
            {
                try
                {
                    await skill.HaltAsync();
                    Logger.LogDebug("HaltAllSkills: HaltAsync called for {SkillName}", skill.Name);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "HaltAllSkills: Error halting skill {SkillName}", skill.Name);
                }
            }

            // Warte bis alle Halted sind
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                var allHalted = skills.All(s => s.CurrentState == SkillStates.Halted);
                
                if (allHalted)
                {
                    Logger.LogInformation("HaltAllSkills: All skills reached Halted state");
                    Set($"allSkillsHalted", true);
                    return NodeStatus.Success;
                }

                await Task.Delay(500);
            }

            // Timeout - log welche Skills nicht Halted sind
            var notHalted = skills.Where(s => s.CurrentState != SkillStates.Halted).ToList();
            Logger.LogError("HaltAllSkills: Timeout - {Count} skills not Halted: {Skills}", 
                notHalted.Count, 
                string.Join(", ", notHalted.Select(s => $"{s.Name}({s.CurrentState})")));
            
            Set($"allSkillsHalted", false);
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HaltAllSkills: Error halting skills");
            return NodeStatus.Failure;
        }
    }
}

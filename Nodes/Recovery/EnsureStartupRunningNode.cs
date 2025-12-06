using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.Recovery;

/// <summary>
/// EnsureStartupRunning - Garantiert dass StartupSkill läuft (idempotent)
/// Prüft State und startet nur wenn nötig
/// </summary>
public class EnsureStartupRunningNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 60;

    public EnsureStartupRunningNode() : base("EnsureStartupRunning")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("EnsureStartupRunning: Ensuring StartupSkill is running in {ModuleName}", ModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("EnsureStartupRunning: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            var client = Context.Get<UaClient>("UaClient");
            if (client?.Session == null)
            {
                Logger.LogError("EnsureStartupRunning: No UaClient Session available");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("EnsureStartupRunning: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            // Finde StartupSkill
            var startupSkill = module.SkillSet.Values
                .FirstOrDefault(s => s.Name.IndexOf("Startup", StringComparison.OrdinalIgnoreCase) >= 0);

            if (startupSkill == null)
            {
                Logger.LogError("EnsureStartupRunning: No StartupSkill found in module");
                return NodeStatus.Failure;
            }

            Logger.LogInformation("EnsureStartupRunning: Found StartupSkill '{SkillName}' with state {State}", 
                startupSkill.Name, startupSkill.CurrentState);

            // Wenn bereits Running → Success (idempotent)
            if (startupSkill.CurrentState == SkillStates.Running)
            {
                Logger.LogInformation("EnsureStartupRunning: StartupSkill already Running");
                Set("startupSkillRunning", true);
                return NodeStatus.Success;
            }

            // Falls Halted oder Completed → Reset → Start
            if (startupSkill.CurrentState == SkillStates.Halted || 
                startupSkill.CurrentState == SkillStates.Completed)
            {
                Logger.LogInformation("EnsureStartupRunning: Resetting StartupSkill from {State}", 
                    startupSkill.CurrentState);
                
                await startupSkill.ResetAsync();
                await Task.Delay(500); // Warte auf Reset
            }

            // Falls Ready oder nach Reset → Start
            if (startupSkill.CurrentState == SkillStates.Ready)
            {
                Logger.LogInformation("EnsureStartupRunning: Starting StartupSkill");
                
                await startupSkill.StartAsync();
                
                // Warte auf Running
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < TimeoutSeconds)
                {
                    var state = await startupSkill.GetStateAsync();
                    
                    if (state.HasValue && state.Value == (int)SkillStates.Running)
                    {
                        Logger.LogInformation("EnsureStartupRunning: StartupSkill reached Running state");
                        Set("startupSkillRunning", true);
                        Set("startupSkillState", "Running");
                        return NodeStatus.Success;
                    }

                    if (state.HasValue && state.Value == (int)SkillStates.Halted)
                    {
                        Logger.LogError("EnsureStartupRunning: StartupSkill went to Halted state");
                        Set("startupSkillRunning", false);
                        return NodeStatus.Failure;
                    }

                    await Task.Delay(500);
                }

                Logger.LogError("EnsureStartupRunning: Timeout waiting for Running state");
                Set("startupSkillRunning", false);
                return NodeStatus.Failure;
            }

            Logger.LogError("EnsureStartupRunning: Unexpected state {State}", startupSkill.CurrentState);
            Set("startupSkillRunning", false);
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "EnsureStartupRunning: Error ensuring startup running");
            Set("startupSkillRunning", false);
            return NodeStatus.Failure;
        }
    }
}

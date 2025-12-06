using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Recovery;

/// <summary>
/// RecoverySequence - Orchestriert komplette Recovery nach Lock-Verlust oder Startup-Halt
/// Führt: HaltAllSkills → EnsureModuleLocked → EnsureStartupRunning aus
/// </summary>
public class RecoverySequenceNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    private HaltAllSkillsNode? _haltAllSkills;
    private EnsureModuleLockedNode? _ensureLocked;
    private EnsureStartupRunningNode? _ensureStartup;

    public RecoverySequenceNode() : base("RecoverySequence")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogWarning("🔄 RecoverySequence: Starting recovery for module {ModuleName}", ModuleName);

        try
        {
            // Schritt 1: Halt All Skills
            Logger.LogInformation("RecoverySequence: Step 1/3 - Halting all skills");
            _haltAllSkills ??= new HaltAllSkillsNode
            {
                ModuleName = ModuleName,
                TimeoutSeconds = 30
            };
            _haltAllSkills.Initialize(Context, Logger);

            var haltResult = await _haltAllSkills.Execute();
            if (haltResult != NodeStatus.Success)
            {
                Logger.LogError("RecoverySequence: Failed to halt all skills");
                Set("recoveryCompleted", false);
                return NodeStatus.Failure;
            }

            Logger.LogInformation("RecoverySequence: ✓ All skills halted");

            // Schritt 2: Ensure Module Locked
            Logger.LogInformation("RecoverySequence: Step 2/3 - Ensuring module is locked");
            _ensureLocked ??= new EnsureModuleLockedNode
            {
                ModuleName = ModuleName,
                ResourceId = ResourceId,
                MaxRetries = 3,
                RetryDelayMs = 2000
            };
            _ensureLocked.Initialize(Context, Logger);

            var lockResult = await _ensureLocked.Execute();
            if (lockResult != NodeStatus.Success)
            {
                Logger.LogError("RecoverySequence: Failed to lock module");
                Set("recoveryCompleted", false);
                return NodeStatus.Failure;
            }

            Logger.LogInformation("RecoverySequence: ✓ Module locked");

            // Schritt 3: Ensure Startup Running
            Logger.LogInformation("RecoverySequence: Step 3/3 - Ensuring StartupSkill is running");
            _ensureStartup ??= new EnsureStartupRunningNode
            {
                ModuleName = ModuleName,
                TimeoutSeconds = 60
            };
            _ensureStartup.Initialize(Context, Logger);

            var startupResult = await _ensureStartup.Execute();
            if (startupResult != NodeStatus.Success)
            {
                Logger.LogError("RecoverySequence: Failed to start StartupSkill");
                Set("recoveryCompleted", false);
                return NodeStatus.Failure;
            }

            Logger.LogInformation("RecoverySequence: ✓ StartupSkill running");

            Logger.LogWarning("✅ RecoverySequence: Recovery completed successfully for {ModuleName}", ModuleName);
            Set("recoveryCompleted", true);
            Context.Set("lastRecoveryTime", DateTime.UtcNow);
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RecoverySequence: Error during recovery");
            Set("recoveryCompleted", false);
            return NodeStatus.Failure;
        }
    }

    public override async Task OnReset()
    {
        await base.OnReset();
        if (_haltAllSkills != null) await _haltAllSkills.OnReset();
        if (_ensureLocked != null) await _ensureLocked.OnReset();
        if (_ensureStartup != null) await _ensureStartup.OnReset();
    }
}

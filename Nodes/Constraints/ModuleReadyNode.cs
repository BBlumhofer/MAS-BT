using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// ModuleReady - Aggregiert mehrere Readiness-Checks (Ready-Flag, keine Fehler, nicht gesperrt, Safety OK)
/// Gibt an ob das Modul sofort Arbeit aufnehmen kann
/// </summary>
public class ModuleReadyNode : BTNode
{
    /// <summary>
    /// Modul-ID das geprüft wird
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    /// <summary>
    /// Ob Safety-Check einbezogen werden soll
    /// </summary>
    public bool CheckSafety { get; set; } = true;
    
    /// <summary>
    /// Ob Lock-Status geprüft werden soll (false = fremde Locks ignorieren)
    /// </summary>
    public bool CheckLock { get; set; } = true;

    public ModuleReadyNode() : base("ModuleReady")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        
        Logger.LogDebug("ModuleReady: Checking readiness of module '{ModuleId}'", moduleId);
        
        try
        {
            var readyState = await CheckReadyFlag(moduleId);
            var errorState = await CheckErrorFlag(moduleId);
            var lockState = CheckLock ? await CheckLockState(moduleId) : true;
            var safetyState = CheckSafety ? await CheckSafetyState(moduleId) : true;
            
            var isReady = readyState && !errorState && lockState && safetyState;
            
            Context.Set($"module_ready_{moduleId}", isReady);
            Context.Set($"module_ready_details_{moduleId}", new Dictionary<string, bool>
            {
                ["ReadyFlag"] = readyState,
                ["NoError"] = !errorState,
                ["NotLocked"] = lockState,
                ["SafetyOk"] = safetyState
            });
            
            if (isReady)
            {
                Logger.LogInformation("ModuleReady: Module '{ModuleId}' is ready for work", moduleId);
                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning("ModuleReady: Module '{ModuleId}' NOT ready. Ready={Ready}, Error={Error}, Locked={Locked}, Safety={Safety}",
                    moduleId, readyState, errorState, !lockState, !safetyState);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ModuleReady: Error checking module readiness");
            return NodeStatus.Failure;
        }
    }
    
    private Task<bool> CheckReadyFlag(string moduleId)
    {
        // Aus RemoteServer/RemoteModule
        if (Context.Has("RemoteServer"))
        {
            var server = Context.Get<dynamic>("RemoteServer");
            if (server?.Modules != null)
            {
                foreach (var module in server.Modules)
                {
                    if (module.Name == moduleId || module.Id == moduleId)
                    {
                        return Task.FromResult(!module.IsLockedByUs);
                    }
                }
            }
        }
        
        // Aus Context State
        if (Context.Has($"ModuleState_{moduleId}"))
        {
            var state = Context.Get<Dictionary<string, object>>($"ModuleState_{moduleId}");
            if (state != null && state.TryGetValue("ModuleReady", out var ready))
            {
                return Task.FromResult(ready is bool b ? b : bool.Parse(ready?.ToString() ?? "false"));
            }
        }
        
        // Default: assume ready
        return Task.FromResult(true);
    }
    
    private Task<bool> CheckErrorFlag(string moduleId)
    {
        if (Context.Has($"module_{moduleId}_has_error"))
        {
            return Task.FromResult(Context.Get<bool>($"module_{moduleId}_has_error"));
        }
        
        if (Context.Has($"ModuleState_{moduleId}"))
        {
            var state = Context.Get<Dictionary<string, object>>($"ModuleState_{moduleId}");
            if (state != null && state.TryGetValue("ErrorCode", out var errorCode))
            {
                var code = Convert.ToInt32(errorCode);
                return Task.FromResult(code != 0);
            }
        }
        
        return Task.FromResult(false);
    }
    
    private Task<bool> CheckLockState(string moduleId)
    {
        // Prüft ob Modul NICHT von anderen gesperrt ist
        if (Context.Has($"module_{moduleId}_locked"))
        {
            var isLocked = Context.Get<bool>($"module_{moduleId}_locked");
            // Wenn von uns gesperrt, ist OK
            if (Context.Has($"module_{moduleId}_locked_by_us"))
            {
                return Task.FromResult(true);
            }
            return Task.FromResult(!isLocked);
        }
        
        return Task.FromResult(true);
    }
    
    private Task<bool> CheckSafetyState(string moduleId)
    {
        if (Context.Has($"safety_ok_{moduleId}"))
        {
            return Task.FromResult(Context.Get<bool>($"safety_ok_{moduleId}"));
        }
        
        if (Context.Has($"ModuleState_{moduleId}"))
        {
            var state = Context.Get<Dictionary<string, object>>($"ModuleState_{moduleId}");
            if (state != null && state.TryGetValue("SafetyOk", out var safety))
            {
                return Task.FromResult(safety is bool b ? b : bool.Parse(safety?.ToString() ?? "true"));
            }
        }
        
        return Task.FromResult(true);
    }
}

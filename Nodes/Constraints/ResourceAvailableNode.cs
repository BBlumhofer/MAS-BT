using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// ResourceAvailable - Prüft ob das Modul zusätzliche Aufgaben annehmen kann
/// Basiert auf Last, Schedule-Belegung und Lock-Status
/// </summary>
public class ResourceAvailableNode : BTNode
{
    /// <summary>
    /// Modul-ID das geprüft wird
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    /// <summary>
    /// Maximale Anzahl paralleler Tasks (0 = unbegrenzt)
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 1;

    public ResourceAvailableNode() : base("ResourceAvailable")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        
        Logger.LogDebug("ResourceAvailable: Checking availability of module '{ModuleId}'", moduleId);
        
        try
        {
            // 1. Lock-Status prüfen
            var isLocked = await CheckLockStatus(moduleId);
            if (isLocked)
            {
                Logger.LogWarning("ResourceAvailable: Module '{ModuleId}' is locked by another agent", moduleId);
                Context.Set($"resource_available_{moduleId}", false);
                return NodeStatus.Failure;
            }
            
            // 2. Aktuelle Auslastung prüfen
            var currentLoad = await GetCurrentLoad(moduleId);
            if (MaxConcurrentTasks > 0 && currentLoad >= MaxConcurrentTasks)
            {
                Logger.LogWarning("ResourceAvailable: Module '{ModuleId}' at max capacity ({Current}/{Max})", 
                    moduleId, currentLoad, MaxConcurrentTasks);
                Context.Set($"resource_available_{moduleId}", false);
                return NodeStatus.Failure;
            }
            
            // 3. Schedule-Verfügbarkeit prüfen
            var hasSlot = await CheckScheduleAvailability(moduleId);
            if (!hasSlot)
            {
                Logger.LogWarning("ResourceAvailable: Module '{ModuleId}' has no available time slots", moduleId);
                Context.Set($"resource_available_{moduleId}", false);
                return NodeStatus.Failure;
            }
            
            Context.Set($"resource_available_{moduleId}", true);
            Context.Set($"resource_load_{moduleId}", currentLoad);
            
            Logger.LogInformation("ResourceAvailable: Module '{ModuleId}' is available (load: {Load}/{Max})", 
                moduleId, currentLoad, MaxConcurrentTasks > 0 ? MaxConcurrentTasks : "∞");
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ResourceAvailable: Error checking resource availability");
            return NodeStatus.Failure;
        }
    }
    
    private Task<bool> CheckLockStatus(string moduleId)
    {
        // Prüfe ob von fremdem Agent gesperrt
        if (Context.Has($"module_{moduleId}_locked"))
        {
            var isLocked = Context.Get<bool>($"module_{moduleId}_locked");
            if (isLocked && !Context.Has($"module_{moduleId}_locked_by_us"))
            {
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }
    
    private Task<int> GetCurrentLoad(string moduleId)
    {
        // Zähle aktive Skills
        if (Context.Has("RemoteServer"))
        {
            var server = Context.Get<dynamic>("RemoteServer");
            if (server?.Modules != null)
            {
                foreach (var module in server.Modules)
                {
                    if (module.Name == moduleId || module.Id == moduleId)
                    {
                        int runningSkills = 0;
                        if (module.SkillSet != null)
                        {
                            foreach (var skill in module.SkillSet)
                            {
                                var state = skill.CurrentState?.ToString() ?? "";
                                if (state == "Running" || state == "Starting" || state == "Executing")
                                {
                                    runningSkills++;
                                }
                            }
                        }
                        return Task.FromResult(runningSkills);
                    }
                }
            }
        }
        
        // Aus Context
        if (Context.Has($"resource_load_{moduleId}"))
        {
            return Task.FromResult(Context.Get<int>($"resource_load_{moduleId}"));
        }
        
        return Task.FromResult(0);
    }
    
    private Task<bool> CheckScheduleAvailability(string moduleId)
    {
        // Prüfe ob freie Zeitslots im Schedule vorhanden
        if (Context.Has($"MachineSchedule_{moduleId}"))
        {
            var schedule = Context.Get<dynamic>($"MachineSchedule_{moduleId}");
            // TODO: Echte Schedule-Analyse wenn API verfügbar
            return Task.FromResult(true);
        }
        
        return Task.FromResult(true);
    }
}

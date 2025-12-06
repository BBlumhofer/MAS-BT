using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes;

/// <summary>
/// CheckReadyState - Prüft ob Modul bereit ist
/// Nutzt RemoteModule Properties
/// </summary>
public class CheckReadyStateNode : BTNode
{
    public string ModuleName { get; set; } = "";
    
    public CheckReadyStateNode() : base("CheckReadyState")
    {
    }
    
    public CheckReadyStateNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("CheckReadyState: Checking if module '{ModuleName}' is ready", ModuleName);
        
        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("CheckReadyState: RemoteServer not found in context");
            return NodeStatus.Failure;
        }
        
        if (!server.Modules.TryGetValue(ModuleName, out var module))
        {
            Logger.LogError("CheckReadyState: Module '{ModuleName}' not found", ModuleName);
            return NodeStatus.Failure;
        }
        
        // Prüfe ob Modul bereit ist (nicht gelockt, keine Fehler)
        // TODO: Wenn RemoteModule.ReadyState Property verfügbar ist, nutze diese
        // Für jetzt: prüfe ob Module connected und nicht locked
        var isReady = !module.IsLockedByUs; // Vereinfachte Ready-Prüfung
        
        Logger.LogInformation("CheckReadyState: Module '{ModuleName}' ready state: {IsReady}", 
            ModuleName, isReady);
        
        Context.Set($"module_{ModuleName}_ready", isReady);
        
        return isReady ? NodeStatus.Success : NodeStatus.Failure;
    }
    
    public override Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// CheckErrorState - Prüft auf Fehler im Modul
/// Erkennt unerwartete Halted States und Error Codes
/// </summary>
public class CheckErrorStateNode : BTNode
{
    public string ModuleName { get; set; } = "";
    
    /// <summary>
    /// Optional: Spezifischer Error Code der erwartet wird
    /// Null = prüfe nur ob überhaupt ein Fehler vorliegt
    /// </summary>
    public int? ExpectedError { get; set; } = null;
    
    public CheckErrorStateNode() : base("CheckErrorState")
    {
    }
    
    public CheckErrorStateNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("CheckErrorState: Checking error state of module '{ModuleName}'", ModuleName);
        
        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("CheckErrorState: RemoteServer not found in context");
            return NodeStatus.Failure;
        }
        
        if (!server.Modules.TryGetValue(ModuleName, out var module))
        {
            Logger.LogError("CheckErrorState: Module '{ModuleName}' not found", ModuleName);
            return NodeStatus.Failure;
        }
        
        // TODO: Prüfe auf Error Code (wenn OPC UA Node verfügbar)
        // Für jetzt: Prüfe ob Skills in Halted State sind (unerwartet)
        
        bool hasError = false;
        
        // Prüfe alle Skills auf unerwartete Halted States
        foreach (var skillKvp in module.SkillSet)
        {
            var skill = skillKvp.Value;
            var state = skill.CurrentState;
            
            // Halted ohne dass wir das wollten = Fehler
            if (state == SkillStates.Halted)
            {
                Logger.LogWarning("CheckErrorState: Skill '{SkillName}' is in Halted state (unexpected error)", 
                    skill.Name);
                hasError = true;
            }
        }
        
        if (ExpectedError.HasValue)
        {
            // Prüfe auf spezifischen Error Code
            // TODO: Implementiere wenn OPC UA Error Node verfügbar
            Logger.LogDebug("CheckErrorState: Specific error code check not yet implemented");
        }
        
        Logger.LogInformation("CheckErrorState: Module '{ModuleName}' has error: {HasError}", 
            ModuleName, hasError);
        
        Context.Set($"module_{ModuleName}_has_error", hasError);
        
        // Success wenn KEIN Fehler vorliegt
        return hasError ? NodeStatus.Failure : NodeStatus.Success;
    }
    
    public override Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// CheckLockedState - Erweiterte Lock-Prüfung
/// Kann prüfen ob Modul gelockt ODER frei ist
/// </summary>
public class CheckLockedStateNode : BTNode
{
    public string ModuleName { get; set; } = "";
    
    /// <summary>
    /// True = erwarte dass Modul gelockt ist
    /// False = erwarte dass Modul frei ist
    /// </summary>
    public bool ExpectLocked { get; set; } = true;
    
    public CheckLockedStateNode() : base("CheckLockedState")
    {
    }
    
    public CheckLockedStateNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("CheckLockedState: Checking lock state of module '{ModuleName}' (expect locked: {ExpectLocked})", 
            ModuleName, ExpectLocked);
        
        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("CheckLockedState: RemoteServer not found in context");
            return NodeStatus.Failure;
        }
        
        if (!server.Modules.TryGetValue(ModuleName, out var module))
        {
            Logger.LogError("CheckLockedState: Module '{ModuleName}' not found", ModuleName);
            return NodeStatus.Failure;
        }
        
        // Prüfe Lock State via RemoteModule.IsLockedByUs
        var isLocked = module.IsLockedByUs;
        
        Logger.LogInformation("CheckLockedState: Module '{ModuleName}' is locked by us: {IsLocked}", 
            ModuleName, isLocked);
        
        Context.Set($"module_{ModuleName}_locked", isLocked);
        
        // Success wenn erwarteter Zustand vorliegt
        bool matchesExpectation = (isLocked == ExpectLocked);
        
        if (!matchesExpectation)
        {
            Logger.LogWarning("CheckLockedState: Module '{ModuleName}' lock state mismatch. Expected locked: {ExpectLocked}, actual: {IsLocked}", 
                ModuleName, ExpectLocked, isLocked);
        }
        
        return matchesExpectation ? NodeStatus.Success : NodeStatus.Failure;
    }
    
    public override Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// MonitoringSkill - Liest Skill State + Monitoring Variables
/// Ermöglicht Closed-Loop Execution Monitoring
/// </summary>
public class MonitoringSkillNode : BTNode
{
    public string ModuleName { get; set; } = "";
    public string SkillName { get; set; } = "";
    
    public MonitoringSkillNode() : base("MonitoringSkill")
    {
    }
    
    public MonitoringSkillNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("MonitoringSkill: Reading monitoring data for skill '{SkillName}' on module '{ModuleName}'", 
            SkillName, ModuleName);
        
        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("MonitoringSkill: RemoteServer not found in context");
            return NodeStatus.Failure;
        }
        
        if (!server.Modules.TryGetValue(ModuleName, out var module))
        {
            Logger.LogError("MonitoringSkill: Module '{ModuleName}' not found", ModuleName);
            return NodeStatus.Failure;
        }
        
        if (!module.SkillSet.TryGetValue(SkillName, out var skill))
        {
            Logger.LogError("MonitoringSkill: Skill '{SkillName}' not found on module '{ModuleName}'", 
                SkillName, ModuleName);
            return NodeStatus.Failure;
        }
        
        // Lese aktuellen State via RemoteSkill.CurrentState Property
        var state = skill.CurrentState;
        
        Logger.LogInformation("MonitoringSkill: Skill '{SkillName}' current state: {State}", 
            SkillName, state);
        
        // Speichere State im Context
        Context.Set($"skill_{SkillName}_state", state.ToString());
        
        // Lese Monitoring Variables (SkillExecution.MonitoringData)
        // TODO: Implementiere wenn MonitoringData Struktur bekannt
        var monitoringData = new Dictionary<string, object>();
        
        try
        {
            // Versuche MonitoringData zu lesen
            // var variables = skill.MonitoringVariables;
            // foreach (var variable in variables)
            // {
            //     monitoringData[variable.Name] = variable.Value;
            // }
            
            Logger.LogDebug("MonitoringSkill: Read {Count} monitoring variables", monitoringData.Count);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MonitoringSkill: Failed to read monitoring variables");
        }
        
        // Speichere Monitoring Data im Context
        Context.Set($"skill_{SkillName}_monitoring", monitoringData);
        
        return NodeStatus.Success;
    }
    
    public override Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        return Task.CompletedTask;
    }
}

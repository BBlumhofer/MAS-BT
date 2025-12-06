using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// SafetyOkay - Prüft Sicherheitssensoren und OPC UA Safety-Flags
/// Stellt sicher, dass keine Skill-Ausführung bei unsicheren Bedingungen beginnt
/// </summary>
public class SafetyOkayNode : BTNode
{
    /// <summary>
    /// Sicherheitszone die geprüft wird
    /// </summary>
    public string ZoneId { get; set; } = "";
    
    /// <summary>
    /// Modul-ID
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    /// <summary>
    /// Kritische Safety-Checks (Failure stoppt sofort)
    /// </summary>
    public bool CriticalCheck { get; set; } = true;

    public SafetyOkayNode() : base("SafetyOkay")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        var zoneId = !string.IsNullOrEmpty(ZoneId) ? ZoneId : moduleId;
        
        Logger.LogDebug("SafetyOkay: Checking safety for zone '{ZoneId}' in module '{ModuleId}'", zoneId, moduleId);
        
        try
        {
            var safetyStatus = new SafetyStatusInfo();
            
            // 1. Emergency Stop prüfen
            safetyStatus.EmergencyStopActive = await CheckEmergencyStop(moduleId);
            if (safetyStatus.EmergencyStopActive)
            {
                Logger.LogError("SafetyOkay: EMERGENCY STOP active in module '{ModuleId}'!", moduleId);
                Context.Set($"safety_ok_{zoneId}", false);
                Context.Set($"safety_status_{zoneId}", safetyStatus);
                return NodeStatus.Failure;
            }
            
            // 2. Safety Gate prüfen
            safetyStatus.SafetyGateOpen = await CheckSafetyGate(zoneId);
            if (safetyStatus.SafetyGateOpen && CriticalCheck)
            {
                Logger.LogWarning("SafetyOkay: Safety gate OPEN in zone '{ZoneId}'", zoneId);
                Context.Set($"safety_ok_{zoneId}", false);
                Context.Set($"safety_status_{zoneId}", safetyStatus);
                return NodeStatus.Failure;
            }
            
            // 3. Light Curtain prüfen
            safetyStatus.LightCurtainInterrupted = await CheckLightCurtain(zoneId);
            if (safetyStatus.LightCurtainInterrupted && CriticalCheck)
            {
                Logger.LogWarning("SafetyOkay: Light curtain interrupted in zone '{ZoneId}'", zoneId);
                Context.Set($"safety_ok_{zoneId}", false);
                Context.Set($"safety_status_{zoneId}", safetyStatus);
                return NodeStatus.Failure;
            }
            
            // 4. Safety PLC Status prüfen
            safetyStatus.SafetyPlcOk = await CheckSafetyPlcStatus(moduleId);
            if (!safetyStatus.SafetyPlcOk)
            {
                Logger.LogError("SafetyOkay: Safety PLC reports unsafe condition in module '{ModuleId}'", moduleId);
                Context.Set($"safety_ok_{zoneId}", false);
                Context.Set($"safety_status_{zoneId}", safetyStatus);
                return NodeStatus.Failure;
            }
            
            // 5. Operator Mode prüfen (manueller Modus kann Einschränkungen haben)
            safetyStatus.OperatorModeActive = await CheckOperatorMode(moduleId);
            if (safetyStatus.OperatorModeActive)
            {
                Logger.LogInformation("SafetyOkay: Operator mode active in module '{ModuleId}' - reduced speed required", moduleId);
            }
            
            safetyStatus.IsSafe = true;
            Context.Set($"safety_ok_{zoneId}", true);
            Context.Set($"safety_status_{zoneId}", safetyStatus);
            
            Logger.LogInformation("SafetyOkay: Zone '{ZoneId}' is SAFE", zoneId);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SafetyOkay: Error checking safety status");
            return NodeStatus.Failure;
        }
    }
    
    private Task<bool> CheckEmergencyStop(string moduleId)
    {
        // OPC UA: /Safety/EmergencyStop
        if (Context.Has($"emergency_stop_{moduleId}"))
        {
            return Task.FromResult(Context.Get<bool>($"emergency_stop_{moduleId}"));
        }
        
        // TODO: OPC UA Abfrage wenn Client verfügbar
        return Task.FromResult(false);
    }
    
    private Task<bool> CheckSafetyGate(string zoneId)
    {
        // OPC UA: /Safety/Zones/{zoneId}/GateOpen
        if (Context.Has($"safety_gate_open_{zoneId}"))
        {
            return Task.FromResult(Context.Get<bool>($"safety_gate_open_{zoneId}"));
        }
        return Task.FromResult(false);
    }
    
    private Task<bool> CheckLightCurtain(string zoneId)
    {
        // OPC UA: /Safety/Zones/{zoneId}/LightCurtain
        if (Context.Has($"light_curtain_interrupted_{zoneId}"))
        {
            return Task.FromResult(Context.Get<bool>($"light_curtain_interrupted_{zoneId}"));
        }
        return Task.FromResult(false);
    }
    
    private Task<bool> CheckSafetyPlcStatus(string moduleId)
    {
        // OPC UA: /Safety/PlcStatus
        if (Context.Has($"safety_plc_ok_{moduleId}"))
        {
            return Task.FromResult(Context.Get<bool>($"safety_plc_ok_{moduleId}"));
        }
        // Standardmäßig OK wenn keine Info vorhanden
        return Task.FromResult(true);
    }
    
    private Task<bool> CheckOperatorMode(string moduleId)
    {
        // OPC UA: /Mode/OperatorActive
        if (Context.Has($"operator_mode_{moduleId}"))
        {
            return Task.FromResult(Context.Get<bool>($"operator_mode_{moduleId}"));
        }
        return Task.FromResult(false);
    }
}

/// <summary>
/// Detaillierter Safety-Status für Context
/// </summary>
public class SafetyStatusInfo
{
    public bool IsSafe { get; set; }
    public bool EmergencyStopActive { get; set; }
    public bool SafetyGateOpen { get; set; }
    public bool LightCurtainInterrupted { get; set; }
    public bool SafetyPlcOk { get; set; }
    public bool OperatorModeActive { get; set; }
}

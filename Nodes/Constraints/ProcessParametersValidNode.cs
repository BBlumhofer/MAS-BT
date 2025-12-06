// filepath: /home/benjamin/AgentDevelopment/MAS-BT/Nodes/Constraints/ProcessParametersValidNode.cs
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// ProcessParametersValid - Evaluiert Prozessparameter gegen erforderliche Constraints
/// Stellt sicher, dass Ausführungsbedingungen korrekt sind bevor ein Skill startet
/// </summary>
public class ProcessParametersValidNode : BTNode
{
    /// <summary>
    /// Modul dessen Parameter geprüft werden
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    /// <summary>
    /// Parameter-Constraints als JSON-String (z.B. {"Temperature": {"min": 20, "max": 80}, "Speed": {"min": 100}})
    /// </summary>
    public string ParameterConstraints { get; set; } = "";
    
    /// <summary>
    /// Skill-Name dessen Parameter geprüft werden sollen (optional)
    /// </summary>
    public string SkillName { get; set; } = "";

    public ProcessParametersValidNode() : base("ProcessParametersValid")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        
        Logger.LogDebug("ProcessParametersValid: Checking parameters for module '{ModuleId}'", moduleId);
        
        try
        {
            if (string.IsNullOrEmpty(ParameterConstraints))
            {
                Logger.LogDebug("ProcessParametersValid: No constraints specified, assuming valid");
                return NodeStatus.Success;
            }
            
            var constraints = ParseConstraints(ParameterConstraints);
            if (constraints == null || constraints.Count == 0)
            {
                Logger.LogWarning("ProcessParametersValid: Failed to parse constraints");
                return NodeStatus.Failure;
            }
            
            var (valid, violations) = await ValidateParameters(moduleId, constraints);
            
            Context.Set($"process_params_valid_{moduleId}", valid);
            Context.Set($"process_params_violations_{moduleId}", violations);
            
            if (valid)
            {
                Logger.LogInformation("ProcessParametersValid: All parameters valid for module '{ModuleId}'", moduleId);
                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning("ProcessParametersValid: Parameter violations in module '{ModuleId}': {Violations}", 
                    moduleId, string.Join(", ", violations));
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ProcessParametersValid: Error validating parameters");
            return NodeStatus.Failure;
        }
    }
    
    private Dictionary<string, ParameterConstraint>? ParseConstraints(string json)
    {
        try
        {
            var result = new Dictionary<string, ParameterConstraint>();
            using var doc = JsonDocument.Parse(json);
            
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var constraint = new ParameterConstraint { Name = prop.Name };
                
                if (prop.Value.TryGetProperty("min", out var minProp))
                    constraint.Min = minProp.GetDouble();
                if (prop.Value.TryGetProperty("max", out var maxProp))
                    constraint.Max = maxProp.GetDouble();
                if (prop.Value.TryGetProperty("equals", out var eqProp))
                    constraint.Equals = eqProp.ToString();
                if (prop.Value.TryGetProperty("notEquals", out var neqProp))
                    constraint.NotEquals = neqProp.ToString();
                if (prop.Value.TryGetProperty("required", out var reqProp))
                    constraint.Required = reqProp.GetBoolean();
                    
                result[prop.Name] = constraint;
            }
            
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ProcessParametersValid: Failed to parse constraints JSON");
            return null;
        }
    }
    
    private Task<(bool valid, List<string> violations)> ValidateParameters(
        string moduleId, 
        Dictionary<string, ParameterConstraint> constraints)
    {
        var violations = new List<string>();
        
        // Aktuelle Parameter aus Context holen
        var currentParams = GetCurrentParameters(moduleId);
        
        foreach (var (paramName, constraint) in constraints)
        {
            if (!currentParams.TryGetValue(paramName, out var actualValue))
            {
                if (constraint.Required)
                {
                    violations.Add($"{paramName}: Required parameter missing");
                }
                continue;
            }
            
            // Numerische Prüfungen
            if (double.TryParse(actualValue?.ToString(), out var numValue))
            {
                if (constraint.Min.HasValue && numValue < constraint.Min.Value)
                {
                    violations.Add($"{paramName}: {numValue} < min({constraint.Min.Value})");
                }
                if (constraint.Max.HasValue && numValue > constraint.Max.Value)
                {
                    violations.Add($"{paramName}: {numValue} > max({constraint.Max.Value})");
                }
            }
            
            // String-Prüfungen
            var strValue = actualValue?.ToString() ?? "";
            if (!string.IsNullOrEmpty(constraint.Equals) && 
                !strValue.Equals(constraint.Equals, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{paramName}: '{strValue}' != expected '{constraint.Equals}'");
            }
            if (!string.IsNullOrEmpty(constraint.NotEquals) && 
                strValue.Equals(constraint.NotEquals, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{paramName}: '{strValue}' should not equal '{constraint.NotEquals}'");
            }
        }
        
        return Task.FromResult((violations.Count == 0, violations));
    }
    
    private Dictionary<string, object?> GetCurrentParameters(string moduleId)
    {
        var result = new Dictionary<string, object?>();
        
        // Aus Skill-Parametern
        if (!string.IsNullOrEmpty(SkillName) && Context.Has($"skill_{SkillName}_params"))
        {
            var skillParams = Context.Get<Dictionary<string, object>>($"skill_{SkillName}_params");
            if (skillParams != null)
            {
                foreach (var kv in skillParams)
                    result[kv.Key] = kv.Value;
            }
        }
        
        // Aus Modul-State
        if (Context.Has($"ModuleState_{moduleId}"))
        {
            var state = Context.Get<Dictionary<string, object>>($"ModuleState_{moduleId}");
            if (state != null)
            {
                foreach (var kv in state)
                    result[kv.Key] = kv.Value;
            }
        }
        
        // Aus Process-Parameters Context
        if (Context.Has($"ProcessParameters_{moduleId}"))
        {
            var processParams = Context.Get<Dictionary<string, object>>($"ProcessParameters_{moduleId}");
            if (processParams != null)
            {
                foreach (var kv in processParams)
                    result[kv.Key] = kv.Value;
            }
        }
        
        return result;
    }
    
    private class ParameterConstraint
    {
        public string Name { get; set; } = "";
        public double? Min { get; set; }
        public double? Max { get; set; }
        public string? Equals { get; set; }
        public string? NotEquals { get; set; }
        public bool Required { get; set; } = false;
    }
}

using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// ExecuteSkill - Führt einen Skill auf einem Remote-Modul aus
/// Nutzt den RemoteServer aus dem Context (muss vorher via ConnectToModule verbunden sein)
/// Verwendet RemoteSkill.ExecuteAsync() mit automatischem Reset bei Halted State
/// </summary>
public class ExecuteSkillNode : BTNode
{
    public string SkillName { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string Parameters { get; set; } = string.Empty; // NEW: z.B. "ProductId=HelloWorld,Param2=Value2"
    public bool WaitForCompletion { get; set; } = true;
    public bool ResetAfterCompletion { get; set; } = true;
    public bool ResetBeforeIfHalted { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;

    public ExecuteSkillNode() : base("ExecuteSkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        // Resolve Placeholders zur Laufzeit
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        var resolvedSkillName = ResolvePlaceholders(SkillName);
        var resolvedParameters = ResolvePlaceholders(Parameters);
        
        Logger.LogInformation("ExecuteSkill: Executing {SkillName} on module {ModuleName}", resolvedSkillName, resolvedModuleName);

        try
        {
            // Hole RemoteServer aus Context (wurde von ConnectToModule gesetzt)
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("ExecuteSkill: No RemoteServer found in context. Connect first with ConnectToModule.");
                Set("started", false);
                return NodeStatus.Failure;
            }

            // Finde Modul
            RemoteModule? module = null;
            if (!string.IsNullOrEmpty(resolvedModuleName))
            {
                if (!server.Modules.TryGetValue(resolvedModuleName, out module))
                {
                    Logger.LogError("ExecuteSkill: Module {ModuleName} not found", resolvedModuleName);
                    Logger.LogDebug("Available modules: {Modules}", string.Join(", ", server.Modules.Keys));
                    Set("started", false);
                    return NodeStatus.Failure;
                }
            }
            else
            {
                // Nimm erstes Modul MIT Skills
                module = server.Modules.Values.FirstOrDefault(m => m.SkillSet.Count > 0);
                if (module == null)
                {
                    Logger.LogError("ExecuteSkill: No modules with skills available on server");
                    Set("started", false);
                    return NodeStatus.Failure;
                }
                Logger.LogDebug("ExecuteSkill: Using first module with skills: {ModuleName} ({SkillCount} skills)", 
                    module.Name, module.SkillSet.Count);
            }

            // Prüfe ob Modul gelockt ist
            var isLocked = Context.Get<bool>($"State_{module.Name}_IsLocked");
            if (!isLocked)
            {
                Logger.LogWarning("ExecuteSkill: Module {ModuleName} is not locked! Lock the module first.", module.Name);
                // Versuche trotzdem fortzufahren - OPC UA Server könnte Lock anders behandeln
            }

            // Finde Skill
            if (!module.SkillSet.TryGetValue(resolvedSkillName, out var skill))
            {
                Logger.LogError("ExecuteSkill: Skill {SkillName} not found on module {ModuleName}", resolvedSkillName, module.Name);
                Logger.LogDebug("Available skills: {Skills}", string.Join(", ", module.SkillSet.Keys));
                Set("started", false);
                return NodeStatus.Failure;
            }

            Logger.LogInformation("ExecuteSkill: Skill {SkillName} found. Current state: {State}", 
                resolvedSkillName, skill.CurrentState);

            // Parse Parameters oder hole aus Context (InputParameters)
            Dictionary<string, object>? parameters = null;
            
            // Priorität 1: Context InputParameters (von ReadMqttSkillRequest gesetzt)
            var contextParams = Context.Get<Dictionary<string, string>>("InputParameters");
            if (contextParams != null && contextParams.Count > 0)
            {
                parameters = new Dictionary<string, object>();
                foreach (var kvp in contextParams)
                {
                    parameters[kvp.Key] = kvp.Value;
                    Logger.LogDebug("ExecuteSkill: Parameter from Context: {Key} = {Value}", kvp.Key, kvp.Value);
                }
            }
            // Priorität 2: Explizite Parameter aus XML (z.B. "ProductId=HelloWorld")
            else if (!string.IsNullOrEmpty(resolvedParameters))
            {
                parameters = new Dictionary<string, object>();
                var paramPairs = resolvedParameters.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in paramPairs)
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        parameters[parts[0].Trim()] = parts[1].Trim();
                        Logger.LogDebug("ExecuteSkill: Parameter from XML: {Key} = {Value}", parts[0].Trim(), parts[1].Trim());
                    }
                }
            }

            // Führe Skill aus via RemoteSkill.ExecuteAsync()
            Logger.LogInformation("ExecuteSkill: Executing skill {SkillName} with {Count} parameters", 
                resolvedSkillName, parameters?.Count ?? 0);
            
            var timeout = TimeSpan.FromSeconds(TimeoutSeconds);
            
            // ExecuteAsync gibt FinalResultData als Dictionary zurück
            dynamic resultDataDynamic = await ((dynamic)skill).ExecuteAsync(
                parameters: parameters,
                waitForCompletion: WaitForCompletion,
                resetAfterCompletion: ResetAfterCompletion,
                resetBeforeIfHalted: ResetBeforeIfHalted,
                timeout: timeout
            );

            // Cast zu Dictionary für static typing
            IDictionary<string, object?>? resultData = resultDataDynamic as IDictionary<string, object?>;

            // Lese FinalResultData nach Completion
            if (WaitForCompletion && resultData != null)
            {
                try
                {
                    // Speichere alle FinalResultData im Context
                    foreach (var kvp in resultData)
                    {
                        var key = kvp.Key;
                        var value = kvp.Value;
                        
                        Logger.LogInformation("ExecuteSkill: Skill {SkillName} - FinalResultData {Key} = {Value}", 
                            resolvedSkillName, key, value?.ToString() ?? "null");
                        
                        Set($"{resolvedSkillName}_{key}", value);
                        Context.Set($"Skill_{resolvedSkillName}_{key}", value);
                    }
                    
                    Context.Set($"Skill_{resolvedSkillName}_FinalResultData", resultData);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "ExecuteSkill: Could not process FinalResultData for skill {SkillName}", resolvedSkillName);
                }
            }

            // Speichere Ergebnis
            Set("started", true);
            Set("lastExecutedSkill", resolvedSkillName);
            
            var finalState = await skill.GetStateAsync();
            var stateName = finalState.HasValue ? ((SkillStates)finalState.Value).ToString() : "Unknown";
            Set($"skill_{resolvedSkillName}_state", stateName);
            
            Logger.LogInformation("ExecuteSkill: Skill {SkillName} execution completed. Final state: {State}", 
                resolvedSkillName, stateName);
            
            return NodeStatus.Success;
        }
        catch (TimeoutException tex)
        {
            Logger.LogError(tex, "ExecuteSkill: Timeout executing skill {SkillName}", resolvedSkillName);
            Set("started", false);
            return NodeStatus.Failure;
        }
        catch (InvalidOperationException ioe)
        {
            // Bei Continuous Skills ohne WaitForCompletion: Prüfe ob Skill bereits läuft
            if (!WaitForCompletion && ioe.Message.Contains("already running"))
            {
                Logger.LogInformation("ExecuteSkill: Skill {SkillName} is already running (continuous skill), treating as success", resolvedSkillName);
                Set("started", true);
                Set("lastExecutedSkill", resolvedSkillName);
                Set($"skill_{resolvedSkillName}_state", "Running");
                return NodeStatus.Success;
            }
            
            Logger.LogError(ioe, "ExecuteSkill: Invalid state for skill {SkillName}", resolvedSkillName);
            Set("started", false);
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ExecuteSkill: Error executing skill {SkillName}", resolvedSkillName);
            Set("started", false);
            return NodeStatus.Failure;
        }
    }
}

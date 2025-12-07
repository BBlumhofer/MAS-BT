using System;
using System.Globalization;
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
        
        Logger.LogDebug("ExecuteSkill: Executing {SkillName} on module {ModuleName}", resolvedSkillName, resolvedModuleName);

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

            // Finde Skill (direkter Lookup, dann heuristische Fallbacks)
            if (!module.SkillSet.TryGetValue(resolvedSkillName, out var skill))
            {
                Logger.LogWarning("ExecuteSkill: Skill {SkillName} not found on module {ModuleName} (direct lookup)", resolvedSkillName, module.Name);
                Logger.LogDebug("ExecuteSkill: Available skills: {Skills}", string.Join(", ", module.SkillSet.Keys));

                // Heuristische Suche: substring match (case-insensitive)
                var found = module.SkillSet.Values.FirstOrDefault(s => !string.IsNullOrEmpty(s.Name) && s.Name.IndexOf(resolvedSkillName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (found == null)
                {
                    // Try appending 'Skill' suffix or trimming 'Skill' from requested name
                    var alt1 = resolvedSkillName.EndsWith("Skill", StringComparison.OrdinalIgnoreCase) ? resolvedSkillName : resolvedSkillName + "Skill";
                    var alt2 = resolvedSkillName.EndsWith("Skill", StringComparison.OrdinalIgnoreCase) ? resolvedSkillName.Substring(0, resolvedSkillName.Length - 5) : null;
                    if (!string.IsNullOrEmpty(alt1)) found = module.SkillSet.Values.FirstOrDefault(s => s.Name.Equals(alt1, StringComparison.OrdinalIgnoreCase) || s.Name.IndexOf(alt1, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (found == null && !string.IsNullOrEmpty(alt2)) found = module.SkillSet.Values.FirstOrDefault(s => s.Name.Equals(alt2, StringComparison.OrdinalIgnoreCase) || s.Name.IndexOf(alt2, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (found != null)
                {
                    Logger.LogWarning("ExecuteSkill: Heuristic matched requested skill '{Requested}' to actual skill '{Actual}'", resolvedSkillName, found.Name);
                    skill = found;
                }
                else
                {
                    Logger.LogError("ExecuteSkill: Skill {SkillName} not found on module {ModuleName} after heuristics", resolvedSkillName, module.Name);
                    Set("started", false);
                    return NodeStatus.Failure;
                }
            }

            Logger.LogDebug("ExecuteSkill: Skill {SkillName} found. Current state: {State}", 
                resolvedSkillName, skill.CurrentState);

            // Parse Parameters oder hole aus Context (InputParameters)
            Dictionary<string, object?>? parameters = null;
            
            // Priorität 1: Context InputParameters (von ReadMqttSkillRequest gesetzt)
            var contextParams = Context.Get<Dictionary<string, object>>("InputParameters");
            if (contextParams != null && contextParams.Count > 0)
            {
                parameters = new Dictionary<string, object?>();
                foreach (var kvp in contextParams)
                {
                    parameters[kvp.Key] = kvp.Value;
                    Logger.LogDebug("ExecuteSkill: Parameter from Context: {Key} = {Value} (Type: {Type})", 
                        kvp.Key, kvp.Value, kvp.Value?.GetType().Name ?? "null");
                }
            }
            // Priorität 2: Explizite Parameter aus XML (z.B. "ProductId=HelloWorld")
            else if (!string.IsNullOrEmpty(resolvedParameters))
            {
                parameters = new Dictionary<string, object?>();
                var paramPairs = resolvedParameters.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in paramPairs)
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var converted = ConvertParameterValue(parts[1].Trim());
                        parameters[key] = converted;
                        Logger.LogDebug("ExecuteSkill: Parameter from XML: {Key} = {Value}", key, converted);
                    }
                }
            }

            // Führe Skill aus via RemoteSkill Client-APIs
            Logger.LogDebug("ExecuteSkill: Executing skill {SkillName} with {Count} parameters", 
                resolvedSkillName, parameters?.Count ?? 0);

            var timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            // Adjust reset flags for continuous skills (e.g. Startup)
            var shouldResetAfterCompletion = ResetAfterCompletion;
            var effectiveResetBefore = ResetBeforeIfHalted;
            try
            {
                if (skill.CurrentState == SkillStates.Running ||
                    (!string.IsNullOrEmpty(skill.Name) && skill.Name.IndexOf("Startup", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    shouldResetAfterCompletion = false;
                    Logger.LogDebug("ExecuteSkill: Disabling ResetAfterCompletion for continuous skill {SkillName}", skill.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "ExecuteSkill: Could not determine skill properties for reset heuristics");
            }

            long? previousSuccessfulExecutions = null;
            if (WaitForCompletion)
            {
                try
                {
                    previousSuccessfulExecutions = await skill.GetSuccessfulExecutionsCountAsync();
                    Logger.LogDebug("ExecuteSkill: Skill {SkillName} SuccessfulExecutionsCount baseline {Count}", resolvedSkillName, previousSuccessfulExecutions?.ToString() ?? "null");
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "ExecuteSkill: Could not read SuccessfulExecutionsCount baseline for skill {SkillName}", resolvedSkillName);
                }
            }

            if (parameters != null && parameters.Count > 0)
            {
                Logger.LogInformation("ExecuteSkill: Preparing to write {Count} parameters to skill {SkillName}", parameters.Count, resolvedSkillName);
                foreach (var param in parameters)
                {
                    var valueType = param.Value?.GetType().Name ?? "null";
                    var valueDisplay = param.Value?.ToString() ?? "<null>";
                    Logger.LogInformation("ExecuteSkill: Parameter '{Key}': Type={Type}, Value={Value}", 
                        param.Key, valueType, valueDisplay);
                }
                
                try
                {
                    await skill.SetInputParametersAsync(parameters);
                    Logger.LogInformation("ExecuteSkill: Successfully wrote all parameters to skill {SkillName}", resolvedSkillName);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "ExecuteSkill: Failed to write parameters for skill {SkillName}", resolvedSkillName);
                    Set("started", false);
                    return NodeStatus.Failure;
                }
            }

            SkillStates? currentState = null;
            try
            {
                var stateValue = await skill.GetStateAsync();
                if (stateValue.HasValue)
                {
                    currentState = (SkillStates)stateValue.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "ExecuteSkill: Unable to read current state for skill {SkillName}", resolvedSkillName);
            }

            if (currentState == SkillStates.Running)
            {
                if (!skill.IsFinite)
                {
                    Logger.LogInformation("ExecuteSkill: Continuous skill {SkillName} already running; skipping Start", resolvedSkillName);
                    Set("started", true);
                    Set("lastExecutedSkill", resolvedSkillName);
                    Context.Set("lastExecutedSkill", resolvedSkillName);
                    Set($"skill_{resolvedSkillName}_state", SkillStates.Running.ToString());
                    return NodeStatus.Success;
                }

                Logger.LogWarning("ExecuteSkill: Finite skill {SkillName} is already running; cannot start again", resolvedSkillName);
                Set("started", false);
                return NodeStatus.Failure;
            }

            // Reset skill if in Halted state before starting
            if (currentState == SkillStates.Halted)
            {
                Logger.LogInformation("ExecuteSkill: Skill {SkillName} is in Halted state. Resetting before start.", resolvedSkillName);
                try
                {
                    await skill.ResetAsync();
                    var readyTimeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 5, 30));
                    var ready = await skill.WaitForStateAsync(SkillStates.Ready, readyTimeout);
                    if (!ready)
                    {
                        Logger.LogError("ExecuteSkill: Skill {SkillName} did not reach Ready after reset from Halted", resolvedSkillName);
                        Set("started", false);
                        return NodeStatus.Failure;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "ExecuteSkill: Failed to reset skill {SkillName} from Halted state", resolvedSkillName);
                    Set("started", false);
                    return NodeStatus.Failure;
                }
            }
            // Also reset if in Completed or Suspended state when WaitForCompletion is enabled
            else if (WaitForCompletion && effectiveResetBefore)
            {
                await TryResetBeforeStartAsync(skill, resolvedSkillName, timeout);
            }

            try
            {
                await skill.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ExecuteSkill: StartAsync failed for skill {SkillName}", resolvedSkillName);
                Set("started", false);
                return NodeStatus.Failure;
            }

            if (WaitForCompletion)
            {
                var targetState = skill.IsFinite ? SkillStates.Completed : SkillStates.Halted;
                var completed = await skill.WaitForStateAsync(targetState, timeout);
                if (!completed)
                {
                    Logger.LogError("ExecuteSkill: Skill {SkillName} did not reach {TargetState} within timeout", resolvedSkillName, targetState);
                    Set("started", false);
                    return NodeStatus.Failure;
                }

                IDictionary<string, object?>? snapshot = null;
                var incrementObserved = false;
                long? actualCount = null;
                try
                {
                    if (previousSuccessfulExecutions.HasValue)
                    {
                        Logger.LogDebug("ExecuteSkill: Waiting for SuccessfulExecutionsCount to increment from baseline {Baseline}", previousSuccessfulExecutions.Value);
                        var snapshotWindowSeconds = Math.Clamp(TimeoutSeconds, 5, 30);
                        long expectedCount = previousSuccessfulExecutions.Value + 1;
                        snapshot = await skill.ReadFinalResultDataAsync(TimeSpan.FromSeconds(snapshotWindowSeconds), expectedCount);

                        if (snapshot != null && snapshot.Count > 0)
                        {
                            actualCount = SkillFinalResultHelper.TryGetSuccessfulExecutionsCount(snapshot);
                            incrementObserved = actualCount.HasValue && actualCount.Value > previousSuccessfulExecutions.Value;
                        }

                        if (!incrementObserved)
                        {
                            Logger.LogWarning("ExecuteSkill: SuccessfulExecutionsCount did not increment (baseline={Baseline}, actual={Actual})", 
                                previousSuccessfulExecutions.Value, actualCount?.ToString() ?? "null");

                            var deadline = DateTime.UtcNow.AddSeconds(20);
                            while (DateTime.UtcNow < deadline)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1));
                                try
                                {
                                    var retrySnapshot = await skill.ReadFinalResultDataSnapshotAsync();
                                    if (retrySnapshot == null || retrySnapshot.Count == 0) continue;

                                    snapshot = retrySnapshot; // keep the freshest snapshot for downstream processing
                                    actualCount = SkillFinalResultHelper.TryGetSuccessfulExecutionsCount(retrySnapshot);
                                    if (actualCount.HasValue && actualCount.Value > previousSuccessfulExecutions.Value)
                                    {
                                        incrementObserved = true;
                                        Logger.LogInformation("ExecuteSkill: SuccessfulExecutionsCount increment detected during polling (baseline={Baseline}, actual={Actual})", 
                                            previousSuccessfulExecutions.Value, actualCount.Value);
                                        break;
                                    }
                                }
                                catch (Exception pollEx)
                                {
                                    Logger.LogDebug(pollEx, "ExecuteSkill: Polling FinalResultData after missing increment failed for skill {SkillName}", resolvedSkillName);
                                }
                            }

                            if (!incrementObserved)
                            {
                                Logger.LogWarning("ExecuteSkill: SuccessfulExecutionsCount did not increment within polling window (baseline={Baseline}, lastSeen={Actual})", 
                                    previousSuccessfulExecutions.Value, actualCount?.ToString() ?? "null");
                            }
                        }
                    }
                    else
                    {
                        var snapshotWindowSeconds = Math.Clamp(TimeoutSeconds, 1, 30);
                        snapshot = await skill.ReadFinalResultDataAsync(TimeSpan.FromSeconds(snapshotWindowSeconds), null);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "ExecuteSkill: Failed to read FinalResultData for skill {SkillName}", resolvedSkillName);
                }

                if (snapshot != null && snapshot.Count > 0)
                {
                    try
                    {
                        var normalized = SkillFinalResultHelper.NormalizeSnapshot(snapshot);
                        foreach (var kvp in normalized)
                        {
                            var key = kvp.Key;
                            var value = kvp.Value;
                            var display = SkillFinalResultHelper.ToDisplayString(value);

                            Logger.LogInformation("ExecuteSkill: Skill {SkillName} FinalResultData {Key} = {Value}",
                                resolvedSkillName, key, string.IsNullOrEmpty(display) ? "<empty>" : display);

                            Set($"{resolvedSkillName}_{key}", value);
                            Context.Set($"Skill_{resolvedSkillName}_{key}", value);
                        }

                        var successfulExecutions = SkillFinalResultHelper.TryGetSuccessfulExecutionsCount(normalized);
                        if (successfulExecutions.HasValue)
                        {
                            Context.Set($"Skill_{resolvedSkillName}_SuccessfulExecutionsCount", successfulExecutions.Value);
                        }

                        Context.Set($"Skill_{resolvedSkillName}_FinalResultData", normalized);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "ExecuteSkill: Could not process FinalResultData for skill {SkillName}", resolvedSkillName);
                    }
                }

                if (shouldResetAfterCompletion)
                {
                    await ResetSkillAfterCompletionAsync(skill, resolvedSkillName, timeout);
                }
            }

            // Speichere Ergebnis
            Set("started", true);
            Set("lastExecutedSkill", resolvedSkillName);
            // Make last executed skill available in shared Context for other nodes (e.g. SendSkillResponse)
            Context.Set("lastExecutedSkill", resolvedSkillName);
            
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

    private async Task TryResetBeforeStartAsync(RemoteSkill skill, string skillName, TimeSpan timeout)
    {
        try
        {
            var stateValue = await skill.GetStateAsync();
            if (!stateValue.HasValue) return;
            var state = (SkillStates)stateValue.Value;
            if (state == SkillStates.Ready) return;

            if (state == SkillStates.Halted || state == SkillStates.Completed || state == SkillStates.Suspended)
            {
                Logger.LogDebug("ExecuteSkill: Skill {SkillName} in state {State} before start. Issuing Reset.", skillName, state);
                await skill.ResetAsync();
                var readyTimeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 5, 30));
                var ready = await skill.WaitForStateAsync(SkillStates.Ready, readyTimeout);
                if (!ready)
                {
                    Logger.LogWarning("ExecuteSkill: Skill {SkillName} did not reach Ready after pre-start reset", skillName);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "ExecuteSkill: Pre-start reset failed for skill {SkillName}", skillName);
        }
    }

    private async Task ResetSkillAfterCompletionAsync(RemoteSkill skill, string skillName, TimeSpan timeout)
    {
        try
        {
            Logger.LogDebug("ExecuteSkill: Resetting skill {SkillName} after capturing FinalResultData", skillName);
            await skill.ResetAsync();

            var readyTimeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 5, 30));

            var ready = await skill.WaitForStateAsync(SkillStates.Ready, readyTimeout);
            if (ready)
            {
                Logger.LogInformation("ExecuteSkill: Skill {SkillName} reset completed", skillName);
            }
            else
            {
                Logger.LogWarning("ExecuteSkill: Skill {SkillName} did not reach Ready after manual reset", skillName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ExecuteSkill: Manual reset after completion failed for skill {SkillName}", skillName);
        }
    }

    private static object? ConvertParameterValue(string? raw)
    {
        if (raw == null) return null;
        var trimmed = raw.Trim();
        if (bool.TryParse(trimmed, out var boolVal)) return boolVal;
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intVal)) return intVal;
        if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longVal)) return longVal;
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var doubleVal)) return doubleVal;
        return raw;
    }
    }

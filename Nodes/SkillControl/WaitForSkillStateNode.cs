using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// WaitForSkillState - Wartet polling-basiert auf einen spezifischen Skill-Status
/// </summary>
public class WaitForSkillStateNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public string TargetState { get; set; } = "Running"; // Default für Continuous Skills
    public int TimeoutSeconds { get; set; } = 60;
    public int PollIntervalMs { get; set; } = 500;
    public bool SendErrorOnTimeout { get; set; } = true; // Automatisch Error Message bei Timeout
    
    private SkillStates? _lastLoggedState = null; // Track last state to avoid log spam

    public WaitForSkillStateNode() : base("WaitForSkillState")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        // Resolve Placeholders zur Laufzeit
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        var resolvedSkillName = ResolvePlaceholders(SkillName);
        
        // Nur beim ersten Aufruf loggen
        if (_lastLoggedState == null)
        {
            Logger.LogInformation("WaitForSkillState: Waiting for {SkillName} to reach state {TargetState} (timeout: {TimeoutSeconds}s)", 
                resolvedSkillName, TargetState, TimeoutSeconds);
        }

        try
        {
            if (!Enum.TryParse<SkillStates>(TargetState, out var targetState))
            {
                Logger.LogError("WaitForSkillState: Invalid target state {TargetState}", TargetState);
                _lastLoggedState = null;
                return NodeStatus.Failure;
            }

            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("WaitForSkillState: No RemoteServer in context");
                _lastLoggedState = null;
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(resolvedModuleName, out var module))
            {
                Logger.LogError("WaitForSkillState: Module {ModuleName} not found", resolvedModuleName);
                _lastLoggedState = null;
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(resolvedSkillName, out var skill))
            {
                Logger.LogWarning("WaitForSkillState: Skill {SkillName} not found in module {ModuleName} (direct lookup)", resolvedSkillName, resolvedModuleName);
                Logger.LogDebug("WaitForSkillState: Available skills: {Skills}", string.Join(", ", module.SkillSet.Keys));

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
                    Logger.LogWarning("WaitForSkillState: Heuristic matched requested skill '{Requested}' to actual skill '{Actual}'", resolvedSkillName, found.Name);
                    skill = found;
                }
                else
                {
                    Logger.LogError("WaitForSkillState: Skill {SkillName} not found in module {ModuleName} after heuristics", resolvedSkillName, resolvedModuleName);
                    _lastLoggedState = null;
                    return NodeStatus.Failure;
                }
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            while (stopwatch.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                var currentState = skill.CurrentState;
                
                // Nur bei State-Change loggen
                if (_lastLoggedState != currentState)
                {
                    Logger.LogDebug("WaitForSkillState: State changed: {OldState} → {NewState} (target: {TargetState})", 
                        _lastLoggedState?.ToString() ?? "null", currentState, targetState);
                    _lastLoggedState = currentState;
                }
                
                // Prüfe auf Halted State (Error)
                if (currentState == SkillStates.Halted)
                {
                    Logger.LogError("WaitForSkillState: Skill {SkillName} entered Halted state (Error)", resolvedSkillName);
                    Set($"skill_{resolvedSkillName}_state", currentState.ToString());
                    Set($"skill_{resolvedSkillName}_error", true);
                    
                    if (SendErrorOnTimeout)
                    {
                        await SendErrorMessage($"Skill {resolvedSkillName} halted during execution");
                    }
                    
                    _lastLoggedState = null;
                    return NodeStatus.Failure;
                }

                if (currentState == targetState)
                {
                    Logger.LogInformation("WaitForSkillState: Skill {SkillName} reached target state {TargetState}", 
                        resolvedSkillName, TargetState);
                    Set($"skill_{resolvedSkillName}_state", currentState.ToString());
                    _lastLoggedState = null; // Reset für nächsten Durchlauf
                    return NodeStatus.Success;
                }

                await Task.Delay(PollIntervalMs);
            }

            // Timeout erreicht
            Logger.LogError("WaitForSkillState: Timeout waiting for {SkillName} to reach state {TargetState}", 
                resolvedSkillName, TargetState);
            Set($"skill_{resolvedSkillName}_state", skill.CurrentState.ToString());
            Set($"skill_{resolvedSkillName}_timeout", true);
            
            if (SendErrorOnTimeout)
            {
                await SendErrorMessage($"Timeout waiting for skill {resolvedSkillName} to reach {TargetState}");
            }
            
            _lastLoggedState = null;
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "WaitForSkillState: Error waiting for skill state");
            
            if (SendErrorOnTimeout)
            {
                await SendErrorMessage($"Exception in WaitForSkillState: {ex.Message}");
            }
            
            _lastLoggedState = null;
            return NodeStatus.Failure;
        }
    }
    
    private async Task SendErrorMessage(string errorMessage)
    {
        try
        {
            var client = Context.Get<I40Sharp.Messaging.MessagingClient>("MessagingClient");
            if (client == null)
            {
                Logger.LogWarning("WaitForSkillState: Cannot send error message - MessagingClient not found");
                return;
            }
            
            var moduleId = Context.Get<string>("ModuleId") ?? "UnknownModule";
            
            // Erstelle Error State Message
            var errorProp = new BaSyx.Models.AdminShell.Property<string>("Error");
            errorProp.Value = new BaSyx.Models.AdminShell.PropertyValue<string>(errorMessage);
            
            var skillNameProp = new BaSyx.Models.AdminShell.Property<string>("SkillName");
            skillNameProp.Value = new BaSyx.Models.AdminShell.PropertyValue<string>(SkillName);
            
            var timestampProp = new BaSyx.Models.AdminShell.Property<string>("Timestamp");
            timestampProp.Value = new BaSyx.Models.AdminShell.PropertyValue<string>(DateTime.UtcNow.ToString("o"));
            
            var message = new I40Sharp.Messaging.Core.I40MessageBuilder()
                .From(Context.AgentId)
                .To("broadcast")
                .WithType("inform")
                .AddElement(errorProp as BaSyx.Models.AdminShell.SubmodelElement)
                .AddElement(skillNameProp as BaSyx.Models.AdminShell.SubmodelElement)
                .AddElement(timestampProp as BaSyx.Models.AdminShell.SubmodelElement)
                .Build();
            
            var topic = TopicHelper.BuildTopic(Context, "Errors");
            await client.PublishAsync(message, topic);
            
            Logger.LogInformation("WaitForSkillState: Error message sent to {Topic}", topic);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "WaitForSkillState: Failed to send error message");
        }
    }
}

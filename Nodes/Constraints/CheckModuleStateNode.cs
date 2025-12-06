using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using AasSharpClient.Models;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// CheckModuleState - Prüft, ob das Modul in einem bestimmten State ist
/// Wenn das Modul bereits einen Skill ausführt (EXECUTING), werden neue Requests abgelehnt
/// </summary>
public class CheckModuleStateNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string ExpectedState { get; set; } = "EXECUTING"; // State, der NICHT sein soll
    public bool InvertCheck { get; set; } = true; // true = Fail wenn State = ExpectedState
    
    public CheckModuleStateNode() : base("CheckModuleState")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        
        // Hole den aktuellen Module State aus dem Context
        var currentModuleState = Context.Get<string>($"ModuleState_{resolvedModuleName}");
        
        if (string.IsNullOrEmpty(currentModuleState))
        {
            // Kein State gesetzt = Modul ist idle
            Logger.LogDebug("CheckModuleState: No state set for module {ModuleName}, assuming idle", resolvedModuleName);
            await Task.CompletedTask;
            return InvertCheck ? NodeStatus.Success : NodeStatus.Failure;
        }
        
        Logger.LogDebug("CheckModuleState: Module {ModuleName} is in state {State}", resolvedModuleName, currentModuleState);
        
        bool stateMatches = string.Equals(currentModuleState, ExpectedState, StringComparison.OrdinalIgnoreCase);
        
        if (InvertCheck)
        {
            // Success wenn State NICHT = ExpectedState (z.B. Modul ist NICHT EXECUTING)
            if (stateMatches)
            {
                Logger.LogWarning("CheckModuleState: Module {ModuleName} is already in state {State}, rejecting new request", 
                    resolvedModuleName, currentModuleState);
                
                // Setze LogMessage für refuse-Nachricht
                Context.Set("LogMessage", $"Module {resolvedModuleName} is already executing a skill");
                
                return NodeStatus.Failure;
            }
            return NodeStatus.Success;
        }
        else
        {
            // Success wenn State = ExpectedState
            return stateMatches ? NodeStatus.Success : NodeStatus.Failure;
        }
    }
}

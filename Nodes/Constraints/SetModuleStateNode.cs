using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using AasSharpClient.Models;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// SetModuleState - Setzt den aktuellen State des Moduls (z.B. EXECUTING, DONE)
/// Wird verwendet um zu tracken, ob ein Modul bereits einen Skill ausführt
/// </summary>
public class SetModuleStateNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty; // EXECUTING, DONE, ERROR, etc.
    
    public SetModuleStateNode() : base("SetModuleState")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        var resolvedState = ResolvePlaceholders(State);
        
        if (string.IsNullOrEmpty(resolvedModuleName) || string.IsNullOrEmpty(resolvedState))
        {
            Logger.LogWarning("SetModuleState: ModuleName or State is empty");
            await Task.CompletedTask;
            return NodeStatus.Failure;
        }
        
        Context.Set($"ModuleState_{resolvedModuleName}", resolvedState);
        Logger.LogInformation("SetModuleState: Module {ModuleName} state set to {State}", resolvedModuleName, resolvedState);
        
        await Task.CompletedTask;
        return NodeStatus.Success;
    }
}

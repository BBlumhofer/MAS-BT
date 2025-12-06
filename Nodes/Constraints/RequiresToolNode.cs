// filepath: /home/benjamin/AgentDevelopment/MAS-BT/Nodes/Constraints/RequiresToolNode.cs
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Constraints;

/// <summary>
/// RequiresTool - Prüft ob ein benötigtes Werkzeug in der Maschine verfügbar ist
/// Hard Constraint für viele Produktionsprozesse
/// </summary>
public class RequiresToolNode : BTNode
{
    /// <summary>
    /// ID des benötigten Werkzeugs
    /// </summary>
    public string ToolId { get; set; } = "";
    
    /// <summary>
    /// Modul in dem das Werkzeug geprüft wird
    /// </summary>
    public string ModuleId { get; set; } = "";
    
    public RequiresToolNode() : base("RequiresTool")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var moduleId = !string.IsNullOrEmpty(ModuleId) ? ModuleId : Context.AgentId ?? "UnknownModule";
        
        Logger.LogDebug("RequiresTool: Checking tool '{ToolId}' in module '{ModuleId}'", ToolId, moduleId);
        
        try
        {
            if (string.IsNullOrEmpty(ToolId))
            {
                Logger.LogWarning("RequiresTool: No ToolId specified");
                return NodeStatus.Failure;
            }
            
            var available = await CheckToolAvailability(moduleId, ToolId);
            
            Context.Set($"tool_{ToolId}_available", available);
            
            if (available)
            {
                Logger.LogInformation("RequiresTool: Tool '{ToolId}' is available in module '{ModuleId}'", 
                    ToolId, moduleId);
                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning("RequiresTool: Tool '{ToolId}' NOT available in module '{ModuleId}'", 
                    ToolId, moduleId);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RequiresTool: Error checking tool availability");
            return NodeStatus.Failure;
        }
    }
    
    private Task<bool> CheckToolAvailability(string moduleId, string toolId)
    {
        // Methode 1: Aus ToolStorage im Context
        if (Context.Has($"ToolStorage_{moduleId}"))
        {
            var toolStorage = Context.Get<List<string>>($"ToolStorage_{moduleId}");
            if (toolStorage != null && toolStorage.Contains(toolId))
            {
                return Task.FromResult(true);
            }
        }
        
        // Methode 2: Aus AvailableTools Dictionary
        if (Context.Has($"AvailableTools_{moduleId}"))
        {
            var tools = Context.Get<Dictionary<string, bool>>($"AvailableTools_{moduleId}");
            if (tools != null && tools.TryGetValue(toolId, out var isAvailable))
            {
                return Task.FromResult(isAvailable);
            }
        }
        
        // Methode 3: Aus Inventory (Tools können auch dort sein)
        if (Context.Has($"Inventory_{moduleId}"))
        {
            var inventory = Context.Get<List<Dictionary<string, object>>>($"Inventory_{moduleId}");
            if (inventory != null)
            {
                foreach (var storage in inventory)
                {
                    if (storage.TryGetValue("name", out var name) && 
                        name?.ToString()?.Contains("Tool", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (storage.TryGetValue("slots", out var slotsObj) && slotsObj is List<object> slots)
                        {
                            foreach (var slotObj in slots)
                            {
                                if (slotObj is Dictionary<string, object> slot &&
                                    slot.TryGetValue("content", out var content) &&
                                    content?.ToString()?.Contains(toolId, StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    return Task.FromResult(true);
                                }
                            }
                        }
                    }
                }
            }
        }
        
        // Methode 4: Aus RemoteModule (OPC UA)
        if (Context.Has("RemoteServer"))
        {
            // TODO: OPC UA Abfrage für Tool-Verfügbarkeit
            // var server = Context.Get<RemoteServer>("RemoteServer");
            // var module = server.GetModule(moduleId);
            // return module?.HasTool(toolId) ?? false;
        }
        
        Logger.LogDebug("RequiresTool: No tool information found for module '{ModuleId}'", moduleId);
        return Task.FromResult(false);
    }
}

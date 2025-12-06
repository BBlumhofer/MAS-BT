using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;

namespace MAS_BT.Nodes.Monitoring;

/// <summary>
/// CheckReadyState - Prüft ob ein Modul bereit für neue Aufgaben ist
/// Liest /State/isReady OPC UA Node und speichert Result im Context
/// </summary>
public class CheckReadyStateNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;

    public CheckReadyStateNode() : base("CheckReadyState")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        // Resolve Placeholders zur Laufzeit (z.B. {MachineName} → "ScrewingStation")
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        
        Logger.LogInformation("CheckReadyState: Checking readiness of module {ModuleName}", resolvedModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("CheckReadyState: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(resolvedModuleName, out var module))
            {
                Logger.LogError("CheckReadyState: Module {ModuleName} not found", resolvedModuleName);
                return NodeStatus.Failure;
            }

            // ✅ Ready Check: Modul ist gelockt UND hat keine Fehler
            // Ein gelocktes Modul ist BEREIT für neue Aufgaben (wir haben Kontrolle)
            bool isLocked = module.IsLockedByUs;
            
            // Prüfe auf Fehler (Skills in Halted State)
            bool hasError = false;
            foreach (var skill in module.SkillSet.Values)
            {
                if (skill.CurrentState == UAClient.Common.SkillStates.Halted)
                {
                    Logger.LogWarning("CheckReadyState: Skill {SkillName} is in Halted state", skill.Name);
                    hasError = true;
                    break;
                }
            }
            
            // Modul ist ready wenn: gelockt UND keine Fehler
            bool isReady = isLocked && !hasError;

            Logger.LogInformation("CheckReadyState: Module {ModuleName} - Locked: {Locked}, HasError: {HasError}, IsReady: {IsReady}", 
                resolvedModuleName, isLocked, hasError, isReady);

            Set($"module_{resolvedModuleName}_ready", isReady);
            Context.Set($"State_{resolvedModuleName}_IsReady", isReady);

            return isReady ? NodeStatus.Success : NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CheckReadyState: Error checking module {ModuleName}", resolvedModuleName);
            return NodeStatus.Failure;
        }
    }
}

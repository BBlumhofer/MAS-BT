using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;

namespace MAS_BT.Nodes.Monitoring;

/// <summary>
/// CheckLockedState - Prüft Lock-Status eines Moduls mit flexiblen Erwartungen
/// Kann validieren, dass Modul gelockt ODER frei ist
/// </summary>
public class CheckLockedStateNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public bool ExpectLocked { get; set; } = true;

    public CheckLockedStateNode() : base("CheckLockedState")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("CheckLockedState: Checking lock state of {ModuleName} (expect locked: {ExpectLocked})", 
            ModuleName, ExpectLocked);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("CheckLockedState: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("CheckLockedState: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            bool isLocked = module.IsLockedByUs;
            bool matches = (isLocked == ExpectLocked);

            Logger.LogInformation("CheckLockedState: Module {ModuleName} lock state: {IsLocked} (expected: {ExpectLocked}, matches: {Matches})", 
                ModuleName, isLocked, ExpectLocked, matches);

            Set($"module_{ModuleName}_locked", isLocked);
            Context.Set($"State_{ModuleName}_IsLocked", isLocked);

            if (!matches)
            {
                Logger.LogWarning("CheckLockedState: Lock state mismatch for {ModuleName}. Expected locked={ExpectLocked} but got {IsLocked}", 
                    ModuleName, ExpectLocked, isLocked);
            }

            return matches ? NodeStatus.Success : NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CheckLockedState: Error checking lock state of {ModuleName}", ModuleName);
            return NodeStatus.Failure;
        }
    }
}

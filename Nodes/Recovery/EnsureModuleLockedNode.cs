using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;

namespace MAS_BT.Nodes.Recovery;

/// <summary>
/// EnsureModuleLocked - Garantiert dass Modul gelockt ist (idempotent)
/// Prüft Lock und lockt nur wenn nötig (mit Retry)
/// </summary>
public class EnsureModuleLockedNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;

    public EnsureModuleLockedNode() : base("EnsureModuleLocked")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("EnsureModuleLocked: Ensuring module {ModuleName} is locked", ModuleName);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("EnsureModuleLocked: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            var client = Context.Get<UaClient>("UaClient");
            if (client?.Session == null)
            {
                Logger.LogError("EnsureModuleLocked: No UaClient Session available");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("EnsureModuleLocked: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            // Wenn bereits gelockt → Success (idempotent)
            if (module.IsLockedByUs)
            {
                Logger.LogInformation("EnsureModuleLocked: Module already locked by us");
                Set("moduleLocked", true);
                return NodeStatus.Success;
            }

            Logger.LogInformation("EnsureModuleLocked: Module not locked, attempting to lock (max {MaxRetries} retries)", MaxRetries);

            // Retry Logic für Lock
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                Logger.LogInformation("EnsureModuleLocked: Lock attempt {Attempt}/{MaxRetries}", attempt, MaxRetries);

                try
                {
                    var lockResult = await module.LockAsync(client.Session);
                    
                    if (lockResult.HasValue && lockResult.Value)
                    {
                        // Verify Lock
                        await Task.Delay(500); // Warte kurz
                        
                        if (module.IsLockedByUs)
                        {
                            Logger.LogInformation("EnsureModuleLocked: Successfully locked module on attempt {Attempt}", attempt);
                            
                            // Update Context
                            Context.Set($"State_{ResourceId}_IsLocked", true);
                            Context.Set($"State_{ResourceId}_LockOwner", Context.AgentId);
                            Context.Set($"State_{ModuleName}_IsLocked", true);
                            Set("moduleLocked", true);
                            Set("lockedModule", ModuleName);
                            
                            return NodeStatus.Success;
                        }
                        else
                        {
                            Logger.LogWarning("EnsureModuleLocked: Lock call succeeded but IsLockedByUs is false");
                        }
                    }
                    else
                    {
                        Logger.LogWarning("EnsureModuleLocked: Lock call returned {Result}", lockResult?.ToString() ?? "null");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "EnsureModuleLocked: Lock attempt {Attempt} failed", attempt);
                }

                // Retry Delay (außer beim letzten Versuch)
                if (attempt < MaxRetries)
                {
                    await Task.Delay(RetryDelayMs);
                }
            }

            Logger.LogError("EnsureModuleLocked: Failed to lock module after {MaxRetries} attempts", MaxRetries);
            Set("moduleLocked", false);
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "EnsureModuleLocked: Error ensuring module locked");
            Set("moduleLocked", false);
            return NodeStatus.Failure;
        }
    }
}

using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using Opc.Ua.Client;
using UAClient.Client;

namespace MAS_BT.Nodes.Locking;

/// <summary>
/// LockResource - Lockt ein Remote-Modul via OPC UA Lock
/// Verwendet RemoteModule.LockAsync() aus dem SkillSharp Client
/// </summary>
public class LockResourceNode : BTNode
{
    public string ResourceId { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public int RetryDelaySeconds { get; set; } = 2;
    
    public LockResourceNode() : base("LockResource")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("LockResource: Attempting to lock module {ModuleName} (ResourceId: {ResourceId})", 
            ModuleName, ResourceId);
        
        try
        {
            // Hole RemoteServer aus Context
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("LockResource: No RemoteServer found in context. Connect first with ConnectToModule.");
                Set("locked", false);
                return NodeStatus.Failure;
            }

            // Hole UaClient Session
            var client = Context.Get<UaClient>("UaClient");
            if (client?.Session == null)
            {
                Logger.LogError("LockResource: No UaClient Session available");
                Set("locked", false);
                return NodeStatus.Failure;
            }

            // Finde Modul
            RemoteModule? module = null;
            if (!string.IsNullOrEmpty(ModuleName))
            {
                if (!server.Modules.TryGetValue(ModuleName, out module))
                {
                    Logger.LogError("LockResource: Module {ModuleName} not found", ModuleName);
                    Logger.LogDebug("Available modules: {Modules}", string.Join(", ", server.Modules.Keys));
                    Set("locked", false);
                    return NodeStatus.Failure;
                }
            }
            else
            {
                // Nimm erstes Modul mit Skills
                module = server.Modules.Values.FirstOrDefault(m => m.SkillSet.Count > 0);
                if (module == null)
                {
                    Logger.LogError("LockResource: No modules available on server");
                    Set("locked", false);
                    return NodeStatus.Failure;
                }
                Logger.LogDebug("LockResource: Using first module with skills: {ModuleName}", module.Name);
            }

            var enableRetry = TimeoutSeconds > 0;
            var timeoutWindow = enableRetry ? TimeSpan.FromSeconds(TimeoutSeconds) : TimeSpan.Zero;
            var retryDelay = TimeSpan.FromSeconds(Math.Max(1, RetryDelaySeconds));
            var deadline = enableRetry ? DateTime.UtcNow + timeoutWindow : DateTime.UtcNow;
            var attempt = 0;

            Logger.LogDebug("LockResource: timeoutSeconds={TimeoutSeconds}, enableRetry={EnableRetry}, retryDelaySeconds={RetryDelaySeconds}", TimeoutSeconds, enableRetry, RetryDelaySeconds);

            while (true)
            {
                attempt++;
                Logger.LogDebug("LockResource: attempt={Attempt}, now={Now:O}, deadline={Deadline:O}", attempt, DateTime.UtcNow, deadline);
                Logger.LogInformation("LockResource: Calling module.LockAsync() for {ModuleName} (attempt {Attempt})", module.Name, attempt);

                var lockResult = await module.LockAsync(client.Session);

                if (lockResult.HasValue && lockResult.Value)
                {
                    Logger.LogInformation("LockResource: Lock call succeeded for {ModuleName}", module.Name);

                    Context.Set($"State_{ResourceId}_IsLocked", true);
                    Context.Set($"State_{ResourceId}_LockOwner", Context.AgentId);
                    Context.Set($"State_{module.Name}_IsLocked", true);
                    Context.Set("locked", true);
                    Context.Set("lockedModule", module.Name);

                    return NodeStatus.Success;
                }

                if (!lockResult.HasValue)
                {
                    Logger.LogError("LockResource: Lock call returned null for {ModuleName}. Aborting.", module.Name);
                    Set("locked", false);
                    return NodeStatus.Failure;
                }

                if (!enableRetry || DateTime.UtcNow >= deadline)
                {
                    Logger.LogError("LockResource: Failed to lock {ModuleName}. Last result {Result}", 
                        module.Name, lockResult.Value);
                    Set("locked", false);
                    return NodeStatus.Failure;
                }

                var ownerInfo = await TryGetLockOwnerAsync(module, client.Session);
                Logger.LogInformation(
                    "LockResource: Module {ModuleName} currently locked by {Owner}. Retrying in {Delay}s (timeout {Timeout}s)",
                    module.Name,
                    string.IsNullOrEmpty(ownerInfo) ? "unknown" : ownerInfo,
                    retryDelay.TotalSeconds,
                    TimeoutSeconds);

                await Task.Delay(retryDelay);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "LockResource: Error locking module");
            Set("locked", false);
            return NodeStatus.Failure;
        }
    }

    private static async Task<string> TryGetLockOwnerAsync(RemoteModule module, Session session)
    {
        if (module.Lock == null || session == null) return string.Empty;
        try
        {
            var owner = await module.Lock.GetLockOwnerAsync(session);
            return string.IsNullOrWhiteSpace(owner) ? string.Empty : owner.Trim('\'', '"', ' ');
        }
        catch
        {
            return string.Empty;
        }
    }
}

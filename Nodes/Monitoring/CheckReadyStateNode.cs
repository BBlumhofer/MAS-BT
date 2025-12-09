using MAS_BT.Core;
using MAS_BT.Services;
using Microsoft.Extensions.Logging;
using UAClient.Client;
using I40Sharp.Messaging;

namespace MAS_BT.Nodes.Monitoring;

/// <summary>
/// CheckReadyState - Prüft ob ein Modul bereit für neue Aufgaben ist
/// Liest /State/isReady OPC UA Node und speichert Result im Context
/// </summary>
public class CheckReadyStateNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public bool RequeueIfUnlocked { get; set; } = true;

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

            if (!isLocked)
            {
                Logger.LogWarning(
                    "CheckReadyState: Module {ModuleName} not locked by this agent. Re-queueing current request.",
                    resolvedModuleName);

                if (RequeueIfUnlocked && await TryRequeueCurrentSkillRequestAsync("module not locked"))
                {
                    // Clear current request from context so other queue entries can be started
                    Context.Set("CurrentSkillRequest", null);
                    Context.Set("CurrentSkillRequestRawMessage", null);
                    Context.Set("InputParameters", new Dictionary<string, object>());
                    return NodeStatus.Failure;
                }

                return NodeStatus.Failure;
            }
            
            // Prüfe auf Fehler (Skills in Halted State)
            // Use live read via GetStateAsync() because cached CurrentState may be uninitialized (defaults to Halted)
            bool hasError = false;
            foreach (var skill in module.SkillSet.Values)
            {
                try
                {
                    var st = await skill.GetStateAsync();
                    if (st == null)
                    {
                        // Unknown state: fall back to cached value but log for diagnostics
                        Logger.LogDebug("CheckReadyState: Skill {SkillName} state unknown (no CurrentState node found), cached={Cached}", skill.Name, skill.CurrentState);
                    }
                    else
                    {
                        // Log numeric state for diagnostics and map to enum when possible
                        Logger.LogDebug("CheckReadyState: Skill {SkillName} numeric state={State}", skill.Name, st);
                        if (st == (int)UAClient.Common.SkillStates.Halted)
                        {
                            Logger.LogWarning("CheckReadyState: Skill {SkillName} is in Halted state", skill.Name);
                            hasError = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "CheckReadyState: Failed to read state for skill {SkillName}, treating as error", skill.Name);
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

    private async Task<bool> TryRequeueCurrentSkillRequestAsync(string reason)
    {
        var currentRequest = Context.Get<SkillRequestEnvelope>("CurrentSkillRequest");
        if (currentRequest == null)
        {
            return false;
        }

        var queue = Context.Get<SkillRequestQueue>("SkillRequestQueue");
        if (queue == null)
        {
            return false;
        }

        if (!queue.TryRequeueByConversationId(currentRequest.ConversationId, out var envelope))
        {
            return false;
        }

        Logger.LogInformation(
            "CheckReadyState: Re-queued conversation '{ConversationId}' because {Reason}",
            currentRequest.ConversationId,
            reason);

        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client != null)
        {
            await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "requeue", envelope);
        }

        Context.Set("SkillRequestQueueLength", queue.Count);
        return true;
    }
}

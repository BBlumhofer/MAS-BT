using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Messages;
using MAS_BT.Core;
using MAS_BT.Services;
using MAS_BT.Nodes.Messaging;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using UAClient.Client;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// CheckSkillPreconditions - Prüft Modul-/Action-Preconditions und steuert Requeue/Backoff.
///</summary>
public class CheckSkillPreconditionsNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;

    public CheckSkillPreconditionsNode() : base("CheckSkillPreconditions")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var resolvedModuleName = ResolvePlaceholders(ModuleName);
        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("CheckSkillPreconditions: No RemoteServer in context");
            return NodeStatus.Failure;
        }

        if (!server.Modules.TryGetValue(resolvedModuleName, out var module))
        {
            Logger.LogError("CheckSkillPreconditions: Module {ModuleName} not found", resolvedModuleName);
            return NodeStatus.Failure;
        }

        var currentRequest = Context.Get<SkillRequestEnvelope>("CurrentSkillRequest");
        var preconditionChecker = new SkillPreconditionChecker(Context, Logger);
        var preconditionResult = await preconditionChecker.EvaluateAsync(module, currentRequest);

        if (preconditionResult.IsSatisfied)
        {
            return NodeStatus.Success;
        }

        var requeueReason = preconditionResult.Errors.FirstOrDefault() ?? "preconditions not satisfied";
        var queue = Context.Get<SkillRequestQueue>("SkillRequestQueue");
        var client = Context.Get<MessagingClient>("MessagingClient");

        if (queue != null && currentRequest != null)
        {
            var maxRetries = Context.Get<int?>("MaxPreconditionRetries") ?? 5;
            if (currentRequest.RetryAttempts >= maxRetries)
            {
                var logMessage = $"Preconditions not satisfied after {currentRequest.RetryAttempts} attempts: {requeueReason}";
                Logger.LogError(
                    "CheckSkillPreconditions: {Message} (conversation {ConversationId})",
                    logMessage,
                    currentRequest.ConversationId);

                Context.Set("LogMessage", logMessage);

                await SendFailureAndRemoveAsync(module.Name, currentRequest, client, queue, logMessage);
                ClearCurrentRequestContext();
                return NodeStatus.Failure;
            }

            var startMs = Context.Get<int?>("PreconditionBackoffStartMs") ?? 5000;
            var attempts = currentRequest.RetryAttempts;
            var backoffMs = (long)(startMs * Math.Pow(2, Math.Max(0, attempts)));
            var backoff = TimeSpan.FromMilliseconds(Math.Min(backoffMs, 5 * 60 * 1000));

            currentRequest.IncrementRetry(backoff);

            if (queue.MoveToEndByConversationId(currentRequest.ConversationId, out var moved))
            {
                if (client != null)
                {
                    try
                    {
                        var actionUpdateMsg = ActionUpdateBuilder.BuildPreconditionUpdate(
                            currentRequest,
                            requeueReason,
                            currentRequest.RetryAttempts,
                            currentRequest.NextRetryUtc);
                        await client.PublishAsync(actionUpdateMsg, TopicHelper.BuildTopic(Context, "SkillResponse"));
                        Logger.LogInformation(
                            "CheckSkillPreconditions: Published ActionUpdate precondition retry for conversation {ConversationId}",
                            currentRequest.ConversationId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "CheckSkillPreconditions: Failed to publish ActionUpdate for precondition retry");
                    }
                }

                Context.Set("SkillRequestQueueLength", queue.Count);
                await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "requeue", moved);

                ClearCurrentRequestContext();
                Context.Set("ActionRequeuedDueToPrecondition", true);
                return NodeStatus.Failure;
            }
        }

        // If we reach here, requeue failed; treat as failure so the tree can proceed to fallback handling
        return NodeStatus.Failure;
    }

    private void ClearCurrentRequestContext()
    {
        Context.Set("CurrentSkillRequest", null);
        Context.Set("CurrentSkillRequestRawMessage", null);
        Context.Set("InputParameters", new Dictionary<string, object>());
    }

    private async Task SendFailureAndRemoveAsync(
        string moduleName,
        SkillRequestEnvelope currentRequest,
        MessagingClient? client,
        SkillRequestQueue queue,
        string logMessage)
    {
        if (client != null)
        {
            try
            {
                var builder = new I40MessageBuilder()
                    .From($"{moduleName}_Execution_Agent", "ExecutionAgent")
                    .To(currentRequest.SenderId, "PlanningAgent")
                    .WithType(I40MessageTypes.FAILURE)
                    .WithConversationId(currentRequest.ConversationId);

                var response = new SkillResponseMessage(
                    ActionStatusEnum.ERROR.ToString(),
                    "error",
                    currentRequest.ActionTitle,
                    currentRequest.MachineName,
                    null,
                    null,
                    null,
                    logMessage,
                    null);

                builder.AddElement(response);

                var message = builder.Build();
                var topic = TopicHelper.BuildTopic(Context, "SkillResponse");
                await client.PublishAsync(message, topic);
                Logger.LogInformation(
                    "CheckSkillPreconditions: Sent FAILURE after exhausting retries for conversation {ConversationId}",
                    currentRequest.ConversationId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "CheckSkillPreconditions: Failed to send FAILURE after exhausting retries");
            }
        }

        if (queue.TryRemoveByConversationId(currentRequest.ConversationId, out var removed))
        {
            await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "remove", removed);
            Context.Set("SkillRequestQueueLength", queue.Count);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.Messages;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Services;

public static class ActionQueueBroadcaster
{
    public static async Task PublishSnapshotAsync(
        BTContext context,
        MessagingClient? client,
        SkillRequestQueue queue,
        string reason,
        SkillRequestEnvelope? changedEnvelope)
    {
        if (client == null || !client.IsConnected || queue == null)
        {
            return;
        }

        try
        {
            var snapshot = queue.Snapshot();
            var queueElement = BuildSnapshotMessage(context, snapshot, reason, changedEnvelope);

            var moduleId = ResolveModuleId(context);
            var agentId = context.AgentId ?? moduleId;
            var agentRole = context.AgentRole ?? "ExecutionAgent";

            var builder = new I40MessageBuilder()
                .From(agentId, agentRole)
                .To("broadcast", string.Empty)
                .WithType(I40MessageTypes.INFORM)
                .WithConversationId(Guid.NewGuid().ToString())
                .AddElement(queueElement);

            var topic = TopicHelper.BuildTopic(context, "ActionQueue");
            await client.PublishAsync(builder.Build(), topic);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ActionQueueBroadcaster: Failed to publish snapshot ({reason}): {ex.Message}");
        }
    }

    private static ActionQueueMessage BuildSnapshotMessage(
        BTContext context,
        IReadOnlyCollection<SkillRequestEnvelope> snapshot,
        string reason,
        SkillRequestEnvelope? changedEnvelope)
    {
        var moduleId = ResolveModuleId(context);
        var entries = snapshot
            .Select((envelope, index) => new ActionQueueEntry
            {
                QueuePosition = index,
                ConversationId = envelope.ConversationId,
                ActionTitle = envelope.ActionTitle,
                ActionState = envelope.QueueState.ToString(),
                EnqueuedAtUtc = envelope.EnqueuedAt,
                ScheduledAtUtc = envelope.StartedAt ?? envelope.EnqueuedAt,
                StartedAtUtc = envelope.StartedAt
            })
            .ToList();

        return new ActionQueueMessage(
            moduleId,
            reason ?? string.Empty,
            changedEnvelope?.ConversationId ?? string.Empty,
            DateTime.UtcNow,
            entries);
    }

    private static string ResolveModuleId(BTContext context)
    {
        if (context == null)
        {
            return "UnknownModule";
        }

        if (context.Has("ModuleId") && context.Get<string>("ModuleId") is { } moduleId && !string.IsNullOrWhiteSpace(moduleId))
        {
            return moduleId;
        }

        if (context.Has("config.Agent.ModuleName") && context.Get<string>("config.Agent.ModuleName") is { } cfgModule && !string.IsNullOrWhiteSpace(cfgModule))
        {
            return cfgModule;
        }

        return context.AgentId ?? "UnknownModule";
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Services;
using AasSharpClient.Models;

namespace MAS_BT.Services;

public static class StepUpdateBroadcaster
{
    public static async Task PublishStepAsync(BTContext context, MessagingClient? client, Step step, string reason = "update")
    {
        if (client == null || !client.IsConnected || step == null || context == null)
        {
            return;
        }

        try
        {
            var moduleId = ResolveModuleId(context);
            var agentId = context.AgentId ?? moduleId;
            var agentRole = context.AgentRole ?? "ExecutionAgent";

            var builder = new I40MessageBuilder()
                .From(agentId, agentRole)
                .To("broadcast", string.Empty)
                .WithType(I40MessageTypes.INFORM)
                .WithConversationId(Guid.NewGuid().ToString())
                .AddElement(step);

            var topic = TopicHelper.BuildTopic(context, "StepUpdate");
            await client.PublishAsync(builder.Build(), topic);
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    private static string ResolveModuleId(BTContext context)
    {
        if (context == null) return "UnknownModule";
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

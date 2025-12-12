using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// Subscribes the planning agent to the topics required for CfP handling.
/// </summary>
public class SubscribePlanningTopicsNode : BTNode
{
    public SubscribePlanningTopicsNode() : base("SubscribePlanningTopics") { }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("SubscribePlanningTopics: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleIdentifiers = ModuleContextHelper.ResolveModuleIdentifiers(Context);
        var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"/{ns}/ProcessChain"
        };

        foreach (var moduleId in moduleIdentifiers)
        {
            topics.Add($"/{ns}/{moduleId}/PlanningAgent/OfferRequest");
        }

        var subscribed = 0;
        foreach (var topic in topics)
        {
            try
            {
                await client.SubscribeAsync(topic);
                subscribed++;
                Logger.LogInformation("SubscribePlanningTopics: subscribed to {Topic}", topic);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SubscribePlanningTopics: failed to subscribe {Topic}", topic);
            }
        }

        return subscribed > 0 ? NodeStatus.Success : NodeStatus.Failure;
    }
}

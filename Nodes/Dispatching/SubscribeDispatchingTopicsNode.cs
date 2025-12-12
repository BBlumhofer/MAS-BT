using System;
using System.Collections.Generic;
using MAS_BT.Core;
using I40Sharp.Messaging;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching
{
    /// <summary>
    /// Subscribes to the relevant MQTT topics for dispatching services.
    /// </summary>
    public class SubscribeDispatchingTopicsNode : BTNode
    {
        public SubscribeDispatchingTopicsNode() : base("SubscribeDispatchingTopics") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("SubscribeDispatchingTopics: MessagingClient unavailable or disconnected");
                return NodeStatus.Failure;
            }

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"/{ns}/ProcessChain",
                $"/{ns}/request/ManufacturingSequence",
                $"/{ns}/request/BookStep",
                $"/{ns}/request/TransportPlan",
                $"/DispatchingAgent/{ns}/ProcessChain/",
                $"/DispatchingAgent/{ns}/RequestManufacturingSequence/",
                $"/DispatchingAgent/{ns}/BookStep/",
                $"/DispatchingAgent/{ns}/RequestTransportPlan/",
                $"/DispatchingAgent/{ns}/ModuleRegistration/"
            };

            var successCount = 0;
            foreach (var topic in topics)
            {
                try
                {
                    await client.SubscribeAsync(topic);
                    successCount++;
                    Logger.LogInformation("SubscribeDispatchingTopics: subscribed to {Topic}", topic);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "SubscribeDispatchingTopics: failed to subscribe {Topic}", topic);
                }
            }

            return successCount > 0 ? NodeStatus.Success : NodeStatus.Failure;
        }
    }
}

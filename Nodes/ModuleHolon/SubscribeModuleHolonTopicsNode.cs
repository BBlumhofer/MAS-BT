using System;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;

namespace MAS_BT.Nodes.ModuleHolon;

public class SubscribeModuleHolonTopicsNode : BTNode
{
    public SubscribeModuleHolonTopicsNode() : base("SubscribeModuleHolonTopics") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("SubscribeModuleHolonTopics: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleId = Context.Get<string>("config.Agent.ModuleName") ?? Context.Get<string>("ModuleId") ?? Context.AgentId;

        var topics = new[]
        {
            $"/{ns}/DispatchingAgent/Offers",
            $"/{ns}/{moduleId}/ScheduleAction",
            $"/{ns}/{moduleId}/BookingConfirmation",
            $"/{ns}/{moduleId}/TransportPlan",
            $"/{ns}/{moduleId}/register",
            $"/{ns}/{moduleId}/Inventory",
            $"/{ns}/{moduleId}/Neighbors"
        };

        var ok = 0;
        foreach (var topic in topics)
        {
            try
            {
                await client.SubscribeAsync(topic);
                ok++;
                Logger.LogInformation("SubscribeModuleHolonTopics: subscribed {Topic}", topic);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SubscribeModuleHolonTopics: failed to subscribe {Topic}", topic);
            }
        }

        // Attach a single message handler (idempotent) to cache inventory/neighbors when they arrive
        if (!Context.Get<bool>("ModuleHolonTopicsHandlerRegistered"))
        {
            client.OnMessage(m =>
            {
                var type = m?.Frame?.Type;
                if (string.Equals(type, "inventoryUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = SerializeMessage(m);
                    Context.Set($"Inventory_{moduleId}", payload);
                    Logger.LogInformation("SubscribeModuleHolonTopics: cached inventory for {ModuleId}", moduleId);
                }
                else if (string.Equals(type, "neighborsUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = SerializeMessage(m);
                    Context.Set($"Neighbors_{moduleId}", payload);
                    Logger.LogInformation("SubscribeModuleHolonTopics: cached neighbors for {ModuleId}", moduleId);
                }
            });
            Context.Set("ModuleHolonTopicsHandlerRegistered", true);
        }

        return ok > 0 ? NodeStatus.Success : NodeStatus.Failure;
    }

    private static string SerializeMessage(I40Sharp.Messaging.Models.I40Message? msg)
    {
        return System.Text.Json.JsonSerializer.Serialize(msg ?? new object());
    }
}

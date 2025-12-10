using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.ModuleHolon;

public class ModuleHolonRegistrationNode : BTNode
{
    public ModuleHolonRegistrationNode() : base("ModuleHolonRegistration") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("ModuleHolonRegistration: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var moduleId = Context.Get<string>("config.Agent.ModuleName")
                       ?? Context.Get<string>("config.Agent.ModuleId")
                       ?? Context.Get<string>("config.OPCUA.ModuleName")
                       ?? Context.Get<string>("ModuleId")
                       ?? Context.AgentId;
        var topic = $"/{ns}/DispatchingAgent/Register";

        try
        {
            var builder = new I40MessageBuilder()
                .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "ModuleHolon" : Context.AgentRole)
                .To("DispatchingAgent", null)
                .WithType("moduleRegistration")
                .WithConversationId(Guid.NewGuid().ToString());

            builder.AddElement(new Property<string>("ModuleId")
            {
                Value = new PropertyValue<string>(moduleId)
            });

            var inventoryJson = Context.Get<string>($"Inventory_{moduleId}");
            if (!string.IsNullOrWhiteSpace(inventoryJson))
            {
                builder.AddElement(new Property<string>("Inventory") { Value = new PropertyValue<string>(inventoryJson) });
            }

            var neighborsJson = Context.Get<string>($"Neighbors_{moduleId}");
            if (!string.IsNullOrWhiteSpace(neighborsJson))
            {
                builder.AddElement(new Property<string>("Neighbors") { Value = new PropertyValue<string>(neighborsJson) });
            }

            var msg = builder.Build();
            await client.PublishAsync(msg, topic);
            Logger.LogInformation("ModuleHolonRegistration: sent registration for {ModuleId} to {Topic}", moduleId, topic);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ModuleHolonRegistration: failed to send registration");
            return NodeStatus.Failure;
        }
    }
}

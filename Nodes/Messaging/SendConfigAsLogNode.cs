using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using System.Text.Json;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// SendConfigAsLog - Sendet die geladene Config via I4.0 Messaging als Log
/// </summary>
public class SendConfigAsLogNode : BTNode
{
    public SendConfigAsLogNode() : base("SendConfigAsLog")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogWarning("SendConfigAsLog: MessagingClient not available, skipping");
                return NodeStatus.Success; // Nicht kritisch
            }

            // Sammle alle Config-Werte aus dem Context
            var configElements = new List<SubmodelElement>();

            // Lese Config-Werte aus Context
            var endpoint = Context.Get<string>("config.OPCUA.Endpoint");
            var username = Context.Get<string>("config.OPCUA.Username");
            var moduleId = Context.Get<string>("config.Agent.ModuleId");

            if (!string.IsNullOrEmpty(endpoint))
            {
                var prop = new Property<string>("OPCUA_Endpoint");
                prop.Value = new PropertyValue<string>(endpoint);
                configElements.Add(prop);
            }

            if (!string.IsNullOrEmpty(username))
            {
                var prop = new Property<string>("OPCUA_Username");
                prop.Value = new PropertyValue<string>(username);
                configElements.Add(prop);
            }

            if (!string.IsNullOrEmpty(moduleId))
            {
                var prop = new Property<string>("ModuleId");
                prop.Value = new PropertyValue<string>(moduleId);
                configElements.Add(prop);
            }

            // Füge Agent-Informationen hinzu
            var agentIdProp = new Property<string>("AgentId");
            agentIdProp.Value = new PropertyValue<string>(Context.AgentId);
            configElements.Add(agentIdProp);

            var agentRoleProp = new Property<string>("AgentRole");
            agentRoleProp.Value = new PropertyValue<string>(Context.AgentRole);
            configElements.Add(agentRoleProp);

            // Erstelle Config-Collection
            var configCollection = new SubmodelElementCollection("AgentConfiguration");
            foreach (var element in configElements)
            {
                configCollection.Add(element);
            }

            // Erstelle I4.0 Message
            var msgTypeProp = new Property<string>("MessageType");
            msgTypeProp.Value = new PropertyValue<string>("ConfigurationReport");
            
            var message = new I40MessageBuilder()
                .From(Context.AgentId)
                .To("broadcast")
                .WithType("inform")
                .AddElement(msgTypeProp)
                .AddElement(configCollection)
                .Build();

            // Sende Config
            var topic = $"{Context.AgentId}/config";
            await client.PublishAsync(message, topic);

            Logger.LogInformation("SendConfigAsLog: Sent configuration to MQTT topic {Topic}", topic);
            Logger.LogDebug("SendConfigAsLog: Config elements sent: {Count}", configElements.Count);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendConfigAsLog: Failed to send configuration");
            return NodeStatus.Failure;
        }
    }
}

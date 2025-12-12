using System;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// ConnectToMessagingBroker - Verbindet mit MQTT Broker via I4.0 Messaging
/// Verwendet I40Sharp.Messaging.MessagingClient
/// </summary>
public class ConnectToMessagingBrokerNode : BTNode
{
    public string BrokerHost { get; set; } = "localhost";
    public int BrokerPort { get; set; } = 1883;
    public string DefaultTopic { get; set; } = "factory/agents/messages";
    public int TimeoutMs { get; set; } = 10000;
    public string ClientId { get; set; } = string.Empty;

    public ConnectToMessagingBrokerNode() : base("ConnectToMessagingBroker")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("ConnectToMessagingBroker: Connecting to {Host}:{Port}", BrokerHost, BrokerPort);

        try
        {
            // Prüfe ob bereits verbunden
            // Ermittel die gewünschte ClientId (aus BT-Input oder Fallback aus Context.AgentId/AgentRole)
            var desiredClientId = string.IsNullOrWhiteSpace(ClientId)
                ? $"{Context.AgentId}_{Context.AgentRole}"
                : ResolvePlaceholders(ClientId);

            var existingClient = Context.Get<MessagingClient>("MessagingClient");
            var existingClientId = Context.Get<string>("MQTTClientId");

            if (existingClient != null && existingClient.IsConnected)
            {
                // Wenn vorhandener Client-ID bekannt und gleich der gewünschten, wiederverwenden
                if (!string.IsNullOrWhiteSpace(existingClientId) && string.Equals(existingClientId, desiredClientId, StringComparison.Ordinal))
                {
                    Logger.LogInformation("ConnectToMessagingBroker: Already connected, reusing existing client (ClientId={ClientId})", existingClientId);
                    Set("messagingConnected", true);
                    Context.Set("MQTTClientId", existingClientId);
                    return NodeStatus.Success;
                }

                // Andernfalls erstellen wir einen neuen MessagingClient mit der gewünschten ClientId
                Logger.LogInformation("ConnectToMessagingBroker: Existing client present (ClientId={Existing}), desired ClientId={Desired} - creating separate client instance.", existingClientId ?? "<unknown>", desiredClientId);
            }

            // Erstelle Transport und Client (unique ClientId per agent/role unless overridden)
            // Verwende die zuvor ermittelte gewünschte ClientId
            var clientId = desiredClientId;

            if (string.IsNullOrWhiteSpace(clientId))
            {
                clientId = $"MASBT_{Guid.NewGuid():N}";
            }

            // Berechne Default-Topic falls nicht gesetzt
            var resolvedDefaultTopic = ResolveDefaultTopic(DefaultTopic);

            Logger.LogInformation("ConnectToMessagingBroker: Using MQTT ClientId {ClientId}", clientId);

            var transport = new MqttTransport(BrokerHost, BrokerPort, clientId);
            var client = new MessagingClient(transport, resolvedDefaultTopic);

            // Event-Handler registrieren
            client.Connected += (s, e) =>
            {
                Logger.LogInformation("MessagingClient: Connected to MQTT broker");
            };

            client.Disconnected += (s, e) =>
            {
                Logger.LogWarning("MessagingClient: Disconnected from MQTT broker");
            };

            // Global Callback für alle Nachrichten (für Debugging)
            client.OnMessage(msg =>
            {
                Logger.LogDebug("MessagingClient: Received message type {Type} from {Sender}",
                    msg.Frame.Type, msg.Frame.Sender.Identification.Id);
            });

            // Zusätzlich: Direkt am Transport die rohe Payload loggen (INFO),
            // damit wir alle eingehenden MQTT-Nachrichten im Agent-Log sehen.
            transport.MessageReceived += (sender, e) =>
            {
                try
                {
                    var topic = e.Topic ?? string.Empty;
                    var payload = e.Payload ?? string.Empty;

                    if (!string.IsNullOrEmpty(topic))
                    {
                        Logger.LogInformation("TransportMessage: Topic={Topic} Payload={...}", topic);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "TransportMessage: failed to log raw payload");
                }
            };

            // Verbinde mit Timeout
            var connectTask = client.ConnectAsync();
            var timeoutTask = Task.Delay(TimeoutMs);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logger.LogError("ConnectToMessagingBroker: Connection timeout after {Timeout}ms", TimeoutMs);
                Set("messagingConnected", false);
                return NodeStatus.Failure;
            }

            await connectTask; // Await to catch exceptions

            // Warte kurz auf erfolgreiche Verbindung
            await Task.Delay(500);

            if (!client.IsConnected)
            {
                Logger.LogError("ConnectToMessagingBroker: Client not connected after connect call");
                Set("messagingConnected", false);
                return NodeStatus.Failure;
            }

            // Zusätzliches Subscription: subscribe auf ProcessChain-Topic (vereinheitlicht für Request/Response)
            try
            {
                var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
                var processChainTopic = $"/{ns}/ProcessChain";
                Logger.LogInformation("ConnectToMessagingBroker: Subscribing to ProcessChain topic {Topic}", processChainTopic);
                await client.SubscribeAsync(processChainTopic);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "ConnectToMessagingBroker: Failed to subscribe to ProcessChain topic");
            }

            // Speichere Client im Context
            Context.Set("MessagingClient", client);
            // Expose the actual client id used for debugging across the tree
            Context.Set("MQTTClientId", clientId);
            Set("messagingConnected", true);
            Set("messagingBroker", $"{BrokerHost}:{BrokerPort}");

            Logger.LogInformation("ConnectToMessagingBroker: Successfully connected to {Host}:{Port}",
                BrokerHost, BrokerPort);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ConnectToMessagingBroker: Failed to connect to broker");
            Set("messagingConnected", false);
            return NodeStatus.Failure;
        }
    }

    private string ResolveDefaultTopic(string configuredTopic)
    {
        if (!string.IsNullOrWhiteSpace(configuredTopic))
        {
            return ResolvePlaceholders(configuredTopic);
        }

        // Fallback: Agent-spezifisches Topic aus Context / Config
        var agentId = Context.Get<string>("AgentId")
                     ?? Context.AgentId
                     ?? Context.Get<string>("config.Agent.AgentId")
                     ?? "Agent";

        // Normalize to avoid MQTT issues with uri characters
        var normalizedAgentId = agentId.Replace(" ", "_");
        return $"{normalizedAgentId}/logs";
    }
}

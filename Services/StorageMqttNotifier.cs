using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using UAClient.Client;
using Opc.Ua.Client;

namespace MAS_BT.Services
{
    /// <summary>
    /// Publishes an MQTT/Interop message whenever a storage variable (or slot variable) changes.
    /// Leverages the Skill-Sharp-Client subscriptions already set up on the RemoteServer.
    /// </summary>
    public class StorageMqttNotifier
    {
        private readonly BTContext _context;
        private readonly RemoteServer _server;
        private readonly MessagingClient _messagingClient;
        private readonly string _agentId;
        private readonly string _agentRole;
        private readonly HashSet<string> _registeredNodes = new(StringComparer.OrdinalIgnoreCase);

        public StorageMqttNotifier(BTContext context, RemoteServer server, MessagingClient messagingClient)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _messagingClient = messagingClient ?? throw new ArgumentNullException(nameof(messagingClient));

            _agentId = _context.AgentId ?? "UnknownAgent";
            _agentRole = _context.AgentRole ?? "ResourceHolon";
        }

        public async Task RegisterAsync()
        {
            var subMgr = _server.SubscriptionManager;
            foreach (var moduleKv in _server.Modules)
            {
                var moduleName = moduleKv.Key;
                var module = moduleKv.Value;
                foreach (var storageKv in module.Storages)
                {
                    var storageName = storageKv.Key;
                    var storage = storageKv.Value;

                    // Storage-level variables
                    foreach (var varKv in storage.Variables)
                    {
                        await RegisterMonitoredItemAsync(subMgr, varKv.Value.NodeId, moduleName, storageName, slotName: null, variableName: varKv.Key);
                    }

                    // Slot-level variables
                    foreach (var slotKv in storage.Slots)
                    {
                        var slotName = slotKv.Key;
                        foreach (var varKv in slotKv.Value.Variables)
                        {
                            await RegisterMonitoredItemAsync(subMgr, varKv.Value.NodeId, moduleName, storageName, slotName, varKv.Key);
                        }
                    }
                }
            }
        }

        private async Task RegisterMonitoredItemAsync(SubscriptionManager subMgr, Opc.Ua.NodeId nodeId, string moduleName, string storageName, string? slotName, string variableName)
        {
            var key = nodeId.ToString();
            if (string.IsNullOrEmpty(key)) return;
            if (_registeredNodes.Contains(key)) return;

            _registeredNodes.Add(key);

            try
            {
                await subMgr.AddMonitoredItemAsync(nodeId, async (_, e) =>
                {
                    try
                    {
                        var notification = e.NotificationValue as MonitoredItemNotification;
                        var dv = notification?.Value;
                        var value = dv?.Value;

                        var path = slotName == null
                            ? $"{moduleName}/{storageName}/{variableName}"
                            : $"{moduleName}/{storageName}/{slotName}/{variableName}";

                        var msgText = $"Storage change on {path}: value={value ?? "<null>"}";
                        var logElement = new LogMessage(LogMessage.LogLevel.Info, msgText, _agentRole, _agentId);
                        var builder = new I40MessageBuilder()
                            .From(_agentId, _agentRole)
                            .To("broadcast", string.Empty)
                            .WithType(I40MessageTypes.INFORM)
                            .WithConversationId(Guid.NewGuid().ToString())
                            .WithMessageId(Guid.NewGuid().ToString())
                            .AddElement(logElement);

                        var topic = $"{_agentId}/storage";
                        await _messagingClient.PublishAsync(builder.Build(), topic);
                    }
                    catch
                    {
                        // ignore individual publish failures
                    }
                });
            }
            catch
            {
                // ignore subscription failures for individual nodes
            }
        }
    }
}

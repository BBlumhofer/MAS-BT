using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using UAClient.Client;
using Opc.Ua.Client;
using Opc.Ua;
using AasSharpClient.Models.Messages;

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
            if (subMgr == null)
            {
                // Without a subscription manager we cannot get on-change updates
                Console.WriteLine("StorageMqttNotifier: SubscriptionManager is null; no storage change subscriptions registered.");
                return;
            }

            var registeredCount = 0;
            foreach (var moduleKv in _server.Modules)
            {
                var moduleName = moduleKv.Key;
                var module = moduleKv.Value;
                if (module.Storages == null || module.Storages.Count == 0)
                {
                    Console.WriteLine($"StorageMqttNotifier: Module '{moduleName}' has no storages to subscribe.");
                }
                foreach (var storageKv in module.Storages ?? new Dictionary<string, RemoteStorage>())
                {
                    var storageName = storageKv.Key;
                    var storage = storageKv.Value;
                    if (storage == null) continue;

                    // Storage-level variables
                    foreach (var varKv in storage.Variables ?? new Dictionary<string, RemoteVariable>())
                    {
                        if (await RegisterMonitoredItemAsync(subMgr, varKv.Value.NodeId, moduleName, storageName, slotName: null, variableName: varKv.Key))
                            registeredCount++;
                    }

                    // Slot-level variables
                    foreach (var slotKv in storage.Slots ?? new Dictionary<string, RemoteStorageSlot>())
                    {
                        var slotName = slotKv.Key;
                        foreach (var varKv in slotKv.Value?.Variables ?? new Dictionary<string, RemoteVariable>())
                        {
                            if (await RegisterMonitoredItemAsync(subMgr, varKv.Value.NodeId, moduleName, storageName, slotName, varKv.Key))
                                registeredCount++;
                        }
                    }
                }
            }

            Console.WriteLine($"StorageMqttNotifier: Registered {registeredCount} storage/slot variable subscriptions.");
            if (registeredCount == 0)
            {
                Console.WriteLine("StorageMqttNotifier: WARNING no subscriptions created — no storage updates will be published.");
            }
        }

        private async Task<bool> RegisterMonitoredItemAsync(SubscriptionManager subMgr, Opc.Ua.NodeId nodeId, string moduleName, string storageName, string? slotName, string variableName)
        {
            var key = nodeId.ToString();
            if (string.IsNullOrEmpty(key)) return false;
            if (_registeredNodes.Contains(key)) return false;

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

                        Console.WriteLine($"StorageMqttNotifier: change for {moduleName}/{storageName}/{slotName ?? "<storage>"}/{variableName}: {value ?? "<null>"}");

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

                        var topic = $"/Modules/{moduleName}/Inventory/";
                        await _messagingClient.PublishAsync(builder.Build(), topic);
                        Console.WriteLine($"StorageMqttNotifier: published storage change to topic {topic}");
                    }
                    catch (Exception pubEx)
                    {
                        Console.WriteLine($"StorageMqttNotifier: publish failed for {moduleName}/{storageName}/{slotName ?? "<storage>"}/{variableName}: {pubEx.Message}");
                    }
                });

                return true;
            }
            catch
            {
                // ignore subscription failures for individual nodes
                return false;
            }
        }
    }
}

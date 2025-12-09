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
        private readonly object _publishLock = new();
        private readonly Dictionary<string, System.Threading.CancellationTokenSource> _pendingPublishes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastEventMessages = new(StringComparer.OrdinalIgnoreCase);
        private int _debounceMs = 150; // coalesce rapid successive changes into a single publish (default)

        public StorageMqttNotifier(BTContext context, RemoteServer server, MessagingClient messagingClient)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _messagingClient = messagingClient ?? throw new ArgumentNullException(nameof(messagingClient));

            _agentId = _context.AgentId ?? "UnknownAgent";
            _agentRole = _context.AgentRole ?? "ResourceHolon";

            // Read optional debounce configuration from context (keys: 'MQTT.DebounceMs' or 'config.MQTT.DebounceMs')
            try
            {
                if (_context.Has("MQTT.DebounceMs"))
                {
                    var v = _context.Get<int>("MQTT.DebounceMs");
                    if (v > 0) _debounceMs = v;
                }
                else if (_context.Has("config.MQTT.DebounceMs"))
                {
                    var v = _context.Get<int>("config.MQTT.DebounceMs");
                    if (v > 0) _debounceMs = v;
                }
            }
            catch { /* ignore config parse errors and keep default */ }
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

        /// <summary>
        /// Graceful shutdown: cancel pending scheduled publishes and optionally flush coalesced events immediately.
        /// </summary>
        public async Task ShutdownAsync(bool flushPending = true)
        {
            List<(string key, string module, string storage, System.Threading.CancellationTokenSource cts)> toCancel = new();
            lock (_publishLock)
            {
                foreach (var kv in _pendingPublishes)
                {
                    var key = kv.Key;
                    var cts = kv.Value;
                    // parse module/storage from key
                    var parts = key.Split('/');
                    var module = parts.Length > 0 ? parts[0] : string.Empty;
                    var storage = parts.Length > 1 ? parts[1] : string.Empty;
                    toCancel.Add((key, module, storage, cts));
                }
                _pendingPublishes.Clear();
            }

            if (toCancel.Count == 0) return;

            if (!flushPending)
            {
                // just cancel and dispose
                foreach (var t in toCancel)
                {
                    try { t.cts.Cancel(); } catch { }
                    try { t.cts.Dispose(); } catch { }
                }
                return;
            }

            // Attempt flush: publish coalesced changes synchronously
            foreach (var t in toCancel)
            {
                try
                {
                    // get last event message if present
                    string last = string.Empty;
                    lock (_publishLock)
                    {
                        _lastEventMessages.TryGetValue(t.key, out last);
                        _lastEventMessages.Remove(t.key);
                    }

                    await PublishCoalescedChangeAsync(t.key, t.module, t.storage, last ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"StorageMqttNotifier: Shutdown publish failed for {t.key}: {ex.Message}");
                }
                finally
                {
                    try { t.cts.Dispose(); } catch { }
                }
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

                        // Use storage-level key for coalescing (avoid multiple publishes per slot)
                        var pathKey = $"{moduleName}/{storageName}";
                        var shortMsg = $"change for {moduleName}/{storageName}/{slotName ?? "<storage>"}/{variableName}: {value ?? "<null>"}";
                        Console.WriteLine($"StorageMqttNotifier: {shortMsg}");

                        // Store last event text for this storage and schedule a coalesced publish
                        SchedulePublish(pathKey, moduleName, storageName, shortMsg);
                    }
                    catch (Exception exOuter)
                    {
                        Console.WriteLine($"StorageMqttNotifier: handler error for {moduleName}/{storageName}/{slotName ?? "<storage>"}/{variableName}: {exOuter.Message}");
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

        private void SchedulePublish(string key, string moduleName, string storageName, string lastEventText)
        {
            System.Threading.CancellationTokenSource? cts = null;
                lock (_publishLock)
                {
                    if (_pendingPublishes.TryGetValue(key, out var existing))
                    {
                        // Cancel existing scheduled publish; do not dispose here because the running task will dispose its own CTS.
                        try { existing.Cancel(); } catch { }
                    }

                    cts = new System.Threading.CancellationTokenSource();
                    _pendingPublishes[key] = cts;
                    _lastEventMessages[key] = lastEventText;
                }

            // Fire-and-forget coalescing task
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceMs, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    string eventText;
                    lock (_publishLock)
                    {
                        _pendingPublishes.Remove(key);
                        _lastEventMessages.TryGetValue(key, out eventText);
                        _lastEventMessages.Remove(key);
                    }

                    await PublishCoalescedChangeAsync(key, moduleName, storageName, eventText ?? string.Empty);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"StorageMqttNotifier: SchedulePublish task failed for {key}: {ex.Message}");
                }
                finally
                {
                    try { cts.Dispose(); } catch { }
                }
            });
        }

        private async Task PublishCoalescedChangeAsync(string key, string moduleName, string storageName, string eventText)
        {
            // Build storage snapshot if available
            var storageUnits = new List<StorageUnit>();
            try
            {
                if (_server.Modules != null && _server.Modules.TryGetValue(moduleName, out var mod) && mod.Storages != null && mod.Storages.TryGetValue(storageName, out var remStorage) && remStorage != null)
                {
                    var slots = new List<Slot>();
                    if (remStorage.Slots != null)
                    {
                        var idx = 0;
                        foreach (var s in remStorage.Slots.Values)
                        {
                            slots.Add(new Slot
                            {
                                Index = idx++,
                                Content = new SlotContent
                                {
                                    CarrierID = s.CarrierId ?? string.Empty,
                                    CarrierType = s.CarrierTypeDisplay() ?? string.Empty,
                                    ProductID = s.ProductId ?? string.Empty,
                                    ProductType = s.ProductTypeDisplay() ?? string.Empty,
                                    IsSlotEmpty = s.IsSlotEmpty ?? true
                                }
                            });
                        }
                    }

                    storageUnits.Add(new StorageUnit
                    {
                        Name = remStorage.Name ?? storageName,
                        Slots = slots
                    });
                }
                else
                {
                    // fallback: single slot with event text as product id
                    storageUnits.Add(new StorageUnit
                    {
                        Name = storageName,
                        Slots = new List<Slot>
                        {
                            new Slot
                            {
                                Index = 0,
                                Content = new SlotContent
                                {
                                    CarrierID = string.Empty,
                                    CarrierType = string.Empty,
                                    ProductID = eventText ?? string.Empty,
                                    ProductType = string.Empty,
                                    IsSlotEmpty = string.IsNullOrEmpty(eventText)
                                }
                            }
                        }
                    });
                }
            }
            catch (Exception mapEx)
            {
                Console.WriteLine($"StorageMqttNotifier: failed to map storage snapshot for {moduleName}/{storageName}: {mapEx.Message}");
            }

            // Publish InventoryMessage
            try
            {
                var inventoryCollection = new InventoryMessage(storageUnits);
                var invBuilder = new I40MessageBuilder()
                    .From(_agentId, _agentRole)
                    .To("broadcast", string.Empty)
                    .WithType(I40MessageTypes.INFORM)
                    .WithConversationId(Guid.NewGuid().ToString())
                    .WithMessageId(Guid.NewGuid().ToString())
                    .AddElement(inventoryCollection);

                var inventoryTopic = $"/Modules/{moduleName}/Inventory/";
                await _messagingClient.PublishAsync(invBuilder.Build(), inventoryTopic);
                Console.WriteLine($"StorageMqttNotifier: published inventory to topic {inventoryTopic}");
            }
            catch (Exception pubEx)
            {
                Console.WriteLine($"StorageMqttNotifier: inventory publish failed for {moduleName}/{storageName} (coalesced): {pubEx.Message}");
            }

            // Publish log message
            try
            {
                var logElement = new LogMessage(LogMessage.LogLevel.Info, eventText ?? string.Empty, _agentRole, _agentId);
                var logBuilder = new I40MessageBuilder()
                    .From(_agentId, _agentRole)
                    .To("broadcast", string.Empty)
                    .WithType(I40MessageTypes.INFORM)
                    .WithConversationId(Guid.NewGuid().ToString())
                    .WithMessageId(Guid.NewGuid().ToString())
                    .AddElement(logElement);

                var logTopic = $"/Modules/{moduleName}/Logs/";
                await _messagingClient.PublishAsync(logBuilder.Build(), logTopic);
                Console.WriteLine($"StorageMqttNotifier: published log to topic {logTopic}");
            }
            catch (Exception logEx)
            {
                Console.WriteLine($"StorageMqttNotifier: log publish failed for {moduleName}/{storageName} (coalesced): {logEx.Message}");
            }
        }
    }
}

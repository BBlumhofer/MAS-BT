using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using MAS_BT.Services;
using I40Sharp.Messaging;
using UAClient.Client;
using UAClient.Common;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// ConnectToModule - Verbindet via OPC UA zu einem Modul-Server
/// Erstellt UaClient + RemoteServer und speichert sie im Context für Wiederverwendung
/// </summary>
public class ConnectToModuleNode : BTNode
{
    public string Endpoint { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 10000;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public ConnectToModuleNode() : base("ConnectToModule")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("ConnectToModule: Connecting to {Endpoint}", Endpoint);

        try
        {
            // Prüfe ob bereits ein RemoteServer im Context existiert
            var existingServer = Context.Get<RemoteServer>("RemoteServer");
            if (existingServer != null)
            {
                // Prüfe Connection-Status
                if (existingServer.Status == RemoteServerStatus.Connected)
                {
                    Logger.LogDebug("ConnectToModule: Already connected to {Endpoint}, reusing existing connection", Endpoint);
                    Set("connected", true);
                    Set("moduleEndpoint", Endpoint);
                    return NodeStatus.Success;
                }
                else
                {
                    Logger.LogWarning("ConnectToModule: Existing server in disconnected state, reconnecting...");
                    try
                    {
                        var reconnectTimeout = TimeoutMs / 1000.0;
                        await existingServer.ConnectAsync(reconnectTimeout);
                        
                        Set("connected", true);
                        Set("moduleEndpoint", Endpoint);
                        Logger.LogInformation("ConnectToModule: Reconnected successfully");
                        return NodeStatus.Success;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "ConnectToModule: Reconnect failed, creating new connection");
                        try { existingServer.Dispose(); } catch { }
                        Context.Remove("RemoteServer");
                        Context.Remove("UaClient");
                    }
                }
            }

            // Erstelle NEUE Verbindung
            Logger.LogInformation("ConnectToModule: Creating new connection to {Endpoint}", Endpoint);
            
            // Hole Username/Password aus Context falls nicht gesetzt
            var username = !string.IsNullOrEmpty(Username) ? Username : Context.Get<string>("config.OPCUA.Username") ?? "";
            var password = !string.IsNullOrEmpty(Password) ? Password : Context.Get<string>("config.OPCUA.Password") ?? "";
            
            Logger.LogDebug("ConnectToModule: Using authentication - Username: {Username}", 
                string.IsNullOrEmpty(username) ? "(anonymous)" : username);
            
            // 1. Erstelle UaClient mit Credentials
            var client = new UaClient(Endpoint, username, password);
            
            // 2. Erstelle RemoteServer (wrapped Client + Auto-Discovery)
            var server = new RemoteServer(client);
            
            // 3. Verbinde (macht automatisch Discovery + Subscriptions)
            var timeoutSeconds = TimeoutMs / 1000.0;
            await server.ConnectAsync(timeoutSeconds);

            // 3b. Warte kurz auf eine stabile Session (Schutz gegen sofortige Disconnects beim Server)
            try
            {
                var maxWait = TimeSpan.FromMilliseconds(TimeoutMs);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < maxWait)
                {
                    if (client.Session != null && client.Session.Connected)
                    {
                        // give a small settle time
                        await Task.Delay(250);
                        if (client.Session != null && client.Session.Connected) break;
                    }
                    await Task.Delay(100);
                }

                if (client.Session == null || !client.Session.Connected)
                {
                    Logger.LogWarning("ConnectToModule: Session did not stabilise after ConnectAsync; continuing but some operations may fail and will be retried by auto-reconnect.");
                }
            }
            catch { }

            // 4. Speichere im Context für andere Nodes (WICHTIG: Nur EINE Instanz!)
            Context.Set("UaClient", client);
            Context.Set("RemoteServer", server);
            Context.Set("moduleEndpoint", Endpoint);
            
            // 5. Aktiviere Auto-Recovery für alle Module (NEW!)
            Logger.LogInformation("ConnectToModule: Enabling auto-recovery for all modules...");
            foreach (var module in server.Modules.Values)
            {
                try
                {
                    await module.EnableAutoRecoveryAsync();
                    Logger.LogInformation("  ✓ Auto-recovery enabled for module '{ModuleName}'", module.Name);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "  ✗ Failed to enable auto-recovery for module '{ModuleName}'", module.Name);
                }
            }
            // 5b. Register RemoteServer MQTT notifier so connection loss/established events are published
            try
            {
                var notifier = new MAS_BT.Services.RemoteServerMqttNotifier(Context);
                server.AddSubscriber(notifier);
                Logger.LogInformation("ConnectToModule: Registered RemoteServer MQTT notifier");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "ConnectToModule: Failed to register RemoteServer MQTT notifier");
            }

            // 5c. Register Storage change MQTT notifier (on-change, no polling)
            try
            {
                var messagingClient = Context.Get<MessagingClient>("MessagingClient");
                if (messagingClient != null)
                {
                    var storageNotifier = new MAS_BT.Services.StorageMqttNotifier(Context, server, messagingClient);
                    await storageNotifier.RegisterAsync();
                    Logger.LogInformation("ConnectToModule: Registered StorageMqttNotifier (on-change storage updates)");
                }
                else
                {
                    Logger.LogWarning("ConnectToModule: MessagingClient not in context; StorageMqttNotifier not registered");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "ConnectToModule: Failed to register StorageMqttNotifier");
            }
            
            Set("connected", true);
            
            Logger.LogInformation("ConnectToModule: Connected successfully to {Endpoint}", Endpoint);
            Logger.LogInformation("  → Discovered {Count} modules", server.Modules.Count);
            
            foreach (var module in server.Modules.Values)
            {
                Logger.LogDebug("  → Module: {ModuleName} ({SkillCount} skills)", module.Name, module.SkillSet.Count);
            }
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ConnectToModule: Connection failed to {Endpoint}", Endpoint);
            Set("connected", false);
            return NodeStatus.Failure;
        }
    }
}

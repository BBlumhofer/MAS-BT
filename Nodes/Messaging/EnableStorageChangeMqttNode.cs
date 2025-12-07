using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Services;
using UAClient.Client;
using I40Sharp.Messaging;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// EnableStorageChangeMqtt - registriert StorageMqttNotifier, um Storage-/Slot-Änderungen per MQTT zu publishen.
/// Erwartet, dass RemoteServer (Skill-Sharp-Client) und MessagingClient im Context liegen.
/// </summary>
public class EnableStorageChangeMqttNode : BTNode
{
    public EnableStorageChangeMqttNode() : base("EnableStorageChangeMqtt") { }

    public override async Task<NodeStatus> Execute()
    {
        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("EnableStorageChangeMqtt: RemoteServer not found in context");
            return NodeStatus.Failure;
        }

        var messagingClient = Context.Get<MessagingClient>("MessagingClient");
        if (messagingClient == null)
        {
            Logger.LogError("EnableStorageChangeMqtt: MessagingClient not found in context");
            return NodeStatus.Failure;
        }

        // avoid duplicate registration in the same run
        if (Context.Has("StorageNotifierRegistered") && Context.Get<bool>("StorageNotifierRegistered"))
        {
            Logger.LogDebug("EnableStorageChangeMqtt: notifier already registered");
            return NodeStatus.Success;
        }

        try
        {
            var notifier = new StorageMqttNotifier(Context, server, messagingClient);
            await notifier.RegisterAsync();
            Context.Set("StorageNotifierRegistered", true);
            Logger.LogInformation("EnableStorageChangeMqtt: Storage change MQTT publishing enabled");
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "EnableStorageChangeMqtt: registration failed");
            return NodeStatus.Failure;
        }
    }
}

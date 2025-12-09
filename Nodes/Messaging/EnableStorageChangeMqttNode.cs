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
        Logger.LogInformation("EnableStorageChangeMqtt: Skipped; storage subscriptions are now registered during ConnectToModule.");
        Context.Set("StorageNotifierRegistered", true);
        await Task.CompletedTask;
        return NodeStatus.Success;
    }
}

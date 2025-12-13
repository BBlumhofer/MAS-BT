using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.ModuleHolon;

public class ReplyToDispatcherNode : BTNode
{
    public string ResponseTopicTemplate { get; set; } = string.Empty;

    public ReplyToDispatcherNode() : base("ReplyToDispatcher") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var msg = Context.Get<I40Message>("PlanningResponse") ?? Context.Get<I40Message>("LastReceivedMessage");
        if (client == null || msg == null)
        {
            Logger.LogError("ReplyToDispatcher: missing client or message");
            return NodeStatus.Failure;
        }

        var topic = Resolve(ResponseTopicTemplate);
        if (string.IsNullOrWhiteSpace(topic))
        {
            Logger.LogError("ReplyToDispatcher: ResponseTopicTemplate empty");
            return NodeStatus.Failure;
        }

        await client.PublishAsync(msg, topic);
        Logger.LogInformation("ReplyToDispatcher: sent response conv {Conv} to {Topic}", msg.Frame?.ConversationId, topic);
        return NodeStatus.Success;
    }

    private string Resolve(string template)
    {
        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleId = ModuleContextHelper.ResolveModuleId(Context);
        return template
            .Replace("{Namespace}", ns)
            .Replace("{ModuleId}", moduleId);
    }
}

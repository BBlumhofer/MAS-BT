using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.ModuleHolon;

public class ForwardToInternalNode : BTNode
{
    public string TargetTopic { get; set; } = string.Empty;

    public ForwardToInternalNode() : base("ForwardToInternal") {}

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("ForwardToInternal: triggerred");
        var client = Context.Get<MessagingClient>("MessagingClient");
        var msg = Context.Get<I40Message>("LastReceivedMessage");
        if (client == null || msg == null)
        {
            Logger.LogError("ForwardToInternal: missing client or message");
            return NodeStatus.Failure;
        }

        var topics = ResolveTopics(TargetTopic).ToList();
        if (topics.Count == 0)
        {
            Logger.LogError("ForwardToInternal: TargetTopic empty");
            return NodeStatus.Failure;
        }

        foreach (var topic in topics)
        {
            await client.PublishAsync(msg, topic);
        }

        Logger.LogInformation("ForwardToInternal: forwarded conversation {Conv} to {Topics}", msg.Frame?.ConversationId, string.Join(", ", topics));
        Context.Set("ForwardedConversationId", msg.Frame?.ConversationId ?? string.Empty);
        return NodeStatus.Success;
    }

    private IEnumerable<string> ResolveTopics(string template)
    {
        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        foreach (var moduleId in ModuleContextHelper.ResolveModuleIdentifiers(Context))
        {
            yield return template
                .Replace("{Namespace}", ns)
                .Replace("{ModuleId}", moduleId);
        }
    }
}

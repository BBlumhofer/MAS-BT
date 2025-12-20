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

        // Resolve topic: prefer template but fall back to selecting ProcessChain vs ManufacturingSequence
        var templateResolved = Resolve(ResponseTopicTemplate);
        string topic;
        if (!string.IsNullOrWhiteSpace(templateResolved))
        {
            // If template references ProcessChain but message is ManufacturingSequence, normalize accordingly
            var incomingType = msg.Frame?.Type ?? string.Empty;
            if (incomingType.IndexOf("ManufacturingSequence", StringComparison.OrdinalIgnoreCase) >= 0 && templateResolved.IndexOf("ProcessChain", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                topic = templateResolved.Replace("ProcessChain", "ManufacturingSequence");
            }
            else
            {
                topic = templateResolved;
            }
        }
        else
        {
            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            topic = $"/{ns}/ManufacturingSequence/Response";
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

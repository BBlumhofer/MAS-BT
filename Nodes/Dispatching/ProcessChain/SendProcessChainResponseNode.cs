using System;
using System.Threading.Tasks;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class SendProcessChainResponseNode : BTNode
{
    public SendProcessChainResponseNode() : base("SendProcessChainResponse") { }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var resultElement = Context.Get<SubmodelElement>("ProcessChain.Result");
        var negotiation = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        var success = Context.Get<bool>("ProcessChain.Success");
        var refusalReason = Context.Get<string>("ProcessChain.RefusalReason");

        if (client == null || negotiation == null)
        {
            Logger.LogError("SendProcessChainResponse: missing client or context");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var requestType = Context.Get<string>("ProcessChain.RequestType") ?? "ProcessChain";
        var isManufacturing = string.Equals(requestType, "ManufacturingSequence", StringComparison.OrdinalIgnoreCase);
        if (resultElement == null && isManufacturing)
        {
            try
            {
                resultElement = Context.Get<SubmodelElement>("ManufacturingSequence.Result");
            }
            catch
            {
                // ignore; fallback handled below
            }
        }

        var topic = $"/{ns}/ManufacturingSequence/Response";
        var messageType = success ? I40MessageTypes.PROPOSAL : I40MessageTypes.REFUSE_PROPOSAL;

        var builder = new I40MessageBuilder()
            .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
            .To(negotiation.RequesterId, null)
            .WithType(messageType)
            .WithConversationId(negotiation.ConversationId);

        if (success && resultElement != null)
        {
            builder.AddElement(resultElement);
        }
        else
        {
            var reason = string.IsNullOrWhiteSpace(refusalReason)
                ? "No capability offers received"
                : refusalReason;
            builder.AddElement(new Property<string>("Reason")
            {
                Value = new PropertyValue<string>(reason)
            });
        }

        await client.PublishAsync(builder.Build(), topic);
        Logger.LogInformation("SendProcessChainResponse: published {Type} ({RequestType}) to {Topic}", messageType, requestType, topic);
        return NodeStatus.Success;
    }
}

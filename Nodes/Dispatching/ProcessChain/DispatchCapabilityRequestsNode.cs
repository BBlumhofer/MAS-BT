using System;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class DispatchCapabilityRequestsNode : BTNode
{
    public DispatchCapabilityRequestsNode() : base("DispatchCapabilityRequests") { }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("DispatchCapabilityRequests: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ctx = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (ctx == null)
        {
            Logger.LogError("DispatchCapabilityRequests: negotiation context missing");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var topic = $"/{ns}/DispatchingAgent/Offer";

        foreach (var requirement in ctx.Requirements)
        {
            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To("Broadcast", "ModuleHolon")
                .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
                .WithConversationId(ctx.ConversationId)
                .AddElement(CreateStringProperty("Capability", requirement.Capability))
                .AddElement(CreateStringProperty("RequirementId", requirement.RequirementId));

            if (!string.IsNullOrWhiteSpace(ctx.ProductId))
            {
                builder.AddElement(CreateStringProperty("ProductId", ctx.ProductId));
            }

            if (requirement.CapabilityContainer != null)
            {
                builder.AddElement(requirement.CapabilityContainer);
            }
            await client.PublishAsync(builder.Build(), topic);
        }

        Logger.LogInformation("DispatchCapabilityRequests: published {Count} CfP messages to {Topic}", ctx.Requirements.Count, topic);
        return NodeStatus.Success;
    }

    private static Property<string> CreateStringProperty(string idShort, string value)
    {
        return new Property<string>(idShort)
        {
            Value = new PropertyValue<string>(value ?? string.Empty)
        };
    }
}

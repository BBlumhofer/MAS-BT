using System;
using System.Threading.Tasks;
using AasSharpClient.Tools;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SendOffer - publishes an offer/proposal message.
/// Prefer passing a fully typed AAS SubmodelElement (e.g. OfferedCapability) via context.
/// </summary>
public class SendOfferNode : BTNode
{
    public string ReceiverId { get; set; } = "broadcast";

    public SendOfferNode() : base("SendOffer")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogWarning("SendOffer: MessagingClient missing, skipping publish");
            return NodeStatus.Success;
        }

        var conv = Context.Get<string>("ConversationId") ?? Guid.NewGuid().ToString();
        var builder = new I40MessageBuilder()
            .From("PlanningAgent", "PlanningAgent")
            .To(ReceiverId, "ProductAgent")
            .WithType(I40MessageTypes.PROPOSAL)
            .WithConversationId(conv);

        // If a full plan was built earlier in the planning flow, send its OfferedCapability (preserves Action/InputParameters etc.)
        var plan = Context.Get<MAS_BT.Nodes.Planning.ProcessChain.CapabilityOfferPlan>("Planning.CapabilityOffer");
        if (plan?.OfferedCapability != null)
        {
            builder.AddElement(plan.OfferedCapability);
            Context.Set("Planning.CapabilityOffer", null);
        }
        else
        {
            var offer = Context.Get<object>("CurrentOffer");
            AttachOffer(builder, offer);
        }

        var message = builder.Build();
        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var offerTopic = $"/{ns}/Offer";
        await client.PublishAsync(message, offerTopic);

        Logger.LogInformation("SendOffer: published offer to topic {Topic} for receiver={Receiver}", offerTopic, ReceiverId);
        return NodeStatus.Success;
    }

    private static void AttachOffer(I40MessageBuilder builder, object? offer)
    {
        if (offer is SubmodelElement sme)
        {
            builder.AddElement(sme);
            return;
        }

        if (offer is System.Text.Json.JsonElement jsonElement)
        {
            var loaded = JsonLoader.DeserializeElement(jsonElement);
            if (loaded is SubmodelElement loadedElement)
            {
                builder.AddElement(loadedElement);
            }
            else
            {
                builder.AddElement(new Property<string>("Payload") { Value = new PropertyValue<string>(jsonElement.GetRawText()) });
            }
            return;
        }

        if (offer is string jsonString)
        {
            var loaded = JsonLoader.DeserializeElement(jsonString);
            if (loaded is SubmodelElement loadedElement)
            {
                builder.AddElement(loadedElement);
            }
            else
            {
                builder.AddElement(new Property<string>("Payload") { Value = new PropertyValue<string>(jsonString) });
            }
            return;
        }

        builder.AddElement(new Property<string>("Payload") { Value = new PropertyValue<string>(offer?.ToString() ?? string.Empty) });
    }
}

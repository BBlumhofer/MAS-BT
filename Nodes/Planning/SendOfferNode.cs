using System;
using System.Threading.Tasks;
using AasSharpClient.Tools;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Tools;
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
            var ok = AttachOffer(builder, offer);
            if (!ok)
            {
                Logger.LogError("SendOffer: CurrentOffer is not a valid AAS SubmodelElement or failed to deserialize; aborting publish.");
                return NodeStatus.Failure;
            }
        }

        var message = builder.Build();
        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var offerTopic = $"/{ns}/ManufacturingSequence/Response";
        await client.PublishAsync(message, offerTopic);

        Logger.LogInformation("SendOffer: published proposal to topic {Topic} for receiver={Receiver}", offerTopic, ReceiverId);
        return NodeStatus.Success;
    }

    private bool AttachOffer(I40MessageBuilder builder, object? offer)
    {
        // Strict mode: only accept already-typed AAS/BaSyx submodel elements or JSON that deserializes
        // into a SubmodelElement via JsonLoader. No permissive fallbacks.
        if (offer is BaSyx.Models.AdminShell.ISubmodelElement ism && ism is BaSyx.Models.AdminShell.SubmodelElement se)
        {
            builder.AddElement(se);
            return true;
        }

        if (offer is string jsonString)
        {
            var loaded = JsonLoader.DeserializeElement(jsonString);
            if (loaded is BaSyx.Models.AdminShell.SubmodelElement loadedElement)
            {
                builder.AddElement(loadedElement);
                return true;
            }
            Logger.LogWarning("SendOffer.AttachOffer: JSON did not deserialize to a SubmodelElement");
            return false;
        }

        // Reject raw object graphs or primitives in strict mode
        Logger.LogWarning("SendOffer.AttachOffer: Offer is not a SubmodelElement or valid JSON string");
        return false;
    }
}

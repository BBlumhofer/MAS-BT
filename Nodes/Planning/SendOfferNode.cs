using System.Text.Json;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SendOffer - stub: publishes a simple offer message (inform) if MessagingClient present.
/// </summary>
public class SendOfferNode : BTNode
{
    public string ReceiverId { get; set; } = "broadcast";

    public SendOfferNode() : base("SendOffer") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var offer = Context.Get<object>("CurrentOffer");
        var conv = Context.Get<string>("ConversationId") ?? System.Guid.NewGuid().ToString();

        if (client == null)
        {
            Logger.LogWarning("SendOffer: MessagingClient missing, skipping publish");
            return NodeStatus.Success;
        }

        var builder = new I40MessageBuilder()
            .From("PlanningAgent", "PlanningAgent")
            .To(ReceiverId, "ProductAgent")
            .WithType(I40MessageTypes.PROPOSAL)
            .WithConversationId(conv);

        // attach offer as SubmodelElement if possible, otherwise serialize to property
        if (offer is SubmodelElement sme)
        {
            builder.AddElement(sme);
        }
        else
        {
            var payload = offer ?? "offer";
            var json = JsonSerializer.Serialize(payload);
            var prop = new Property<string>("OfferPayload")
            {
                Value = new PropertyValue<string>(json)
            };
            builder.AddElement(prop);
        }
        var message = builder.Build();
        await client.PublishAsync(message, $"/Planning/Offers/{ReceiverId}/");
        Logger.LogInformation("SendOffer: published offer to {Receiver}", ReceiverId);
        return NodeStatus.Success;
    }
}

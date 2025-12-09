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
/// SendPlanningRefusal - sends a REFUSAL to the requester/product agent.
/// </summary>
public class SendPlanningRefusalNode : BTNode
{
    public string ReceiverId { get; set; } = "ProductAgent";
    public string Reason { get; set; } = "unspecified";

    public SendPlanningRefusalNode() : base("SendPlanningRefusal") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogWarning("SendPlanningRefusal: MessagingClient missing, skipping publish");
            return NodeStatus.Success;
        }

        var conv = Context.Get<string>("ConversationId") ?? System.Guid.NewGuid().ToString();
        var refusalReason = Context.Get<string>("RefusalReason") ?? Reason;

        var builder = new I40MessageBuilder()
            .From("PlanningAgent", "PlanningAgent")
            .To(ReceiverId, "ProductAgent")
            .WithType(I40MessageTypes.REFUSAL)
            .WithConversationId(conv);

        var prop = new Property<string>("RefusalReason")
        {
            Value = new PropertyValue<string>(JsonSerializer.Serialize(refusalReason))
        };
        builder.AddElement(prop);

        var message = builder.Build();
        await client.PublishAsync(message, $"/Planning/Refusals/{ReceiverId}/");
        Logger.LogInformation("SendPlanningRefusal: published refusal to {Receiver} with reason {Reason}", ReceiverId, refusalReason);
        return NodeStatus.Success;
    }
}

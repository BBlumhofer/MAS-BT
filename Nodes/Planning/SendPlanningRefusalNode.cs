using System.Text.Json;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning.ProcessChain;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SendPlanningRefusal - sends a REFUSAL to the requester/product agent.
/// </summary>
public class SendPlanningRefusalNode : BTNode
{
    public string ReceiverId { get; set; } = "{RequesterId}";
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
        var failureDetail = Context.Get<string>("CapabilityMatchmaking.FailureDetail");
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");

        var resolvedReceiver = ResolvePlaceholders(ReceiverId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(resolvedReceiver))
        {
            resolvedReceiver = request?.RequesterId ?? "ProductAgent";
        }

        var builder = new I40MessageBuilder()
            .From("PlanningAgent", "PlanningAgent")
            .To(resolvedReceiver, "ProductAgent")
            .WithType(I40MessageTypes.REFUSAL)
            .WithConversationId(conv);

        var prop = new Property<string>("RefusalReason")
        {
            Value = new PropertyValue<string>(JsonSerializer.Serialize(refusalReason))
        };
        builder.AddElement(prop);

        if (!string.IsNullOrWhiteSpace(failureDetail))
        {
            var detailProp = new Property<string>("FailureDetail")
            {
                Value = new PropertyValue<string>(JsonSerializer.Serialize(failureDetail))
            };
            builder.AddElement(detailProp);
        }

        var message = builder.Build();
        await client.PublishAsync(message, $"/Planning/Refusals/{resolvedReceiver}/");
        Logger.LogInformation("SendPlanningRefusal: published refusal to {Receiver} with reason {Reason}", resolvedReceiver, refusalReason);
        return NodeStatus.Success;
    }
}

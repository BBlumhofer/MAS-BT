using System;
using System.Globalization;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning.ProcessChain;

public class SendCapabilityOfferNode : BTNode
{
    public SendCapabilityOfferNode() : base("SendCapabilityOffer") { }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        var plan = Context.Get<CapabilityOfferPlan>("Planning.CapabilityOffer");

        if (client == null || request == null || plan == null)
        {
            Logger.LogError("SendCapabilityOffer: missing client ({ClientMissing}) or context", client == null);
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleId = Context.Get<string>("ModuleId") ?? Context.Get<string>("config.Agent.ModuleName") ?? Context.AgentId;
        var topic = $"/{ns}/{moduleId}/PlanningAgent/OfferResponse";

        var builder = new I40MessageBuilder()
            .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "PlanningAgent" : Context.AgentRole)
            .To(request.RequesterId, null)
            .WithType(I40Sharp.Messaging.Models.I40MessageTypes.PROPOSAL)
            .WithConversationId(request.ConversationId)
            .AddElement(CreateStringProperty("Capability", request.Capability))
            .AddElement(CreateStringProperty("RequirementId", request.RequirementId))
            .AddElement(CreateStringProperty("OfferId", plan.OfferId))
            .AddElement(CreateStringProperty("Station", plan.StationId))
            .AddElement(CreateStringProperty("EarliestStartUtc", plan.StartTimeUtc.ToString("o")))
            .AddElement(CreateStringProperty("CycleTimeMinutes", plan.CycleTime.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)))
            .AddElement(CreateStringProperty("SetupTimeMinutes", plan.SetupTime.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)))
            .AddElement(CreateStringProperty("Cost", plan.Cost.ToString("0.##", CultureInfo.InvariantCulture)));

        if (!string.IsNullOrWhiteSpace(request.ProductId))
        {
            builder.AddElement(CreateStringProperty("ProductId", request.ProductId));
        }

        var message = builder.Build();
        await client.PublishAsync(message, topic);
        Logger.LogInformation("SendCapabilityOffer: sent proposal {OfferId} for requirement {Requirement}", plan.OfferId, request.RequirementId);
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

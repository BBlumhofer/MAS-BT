using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models.Messages;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
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

        var offeredCapability = plan.OfferedCapability;
        if (offeredCapability == null)
        {
            Logger.LogError("SendCapabilityOffer: offered capability missing for requirement {Requirement}", request.RequirementId);
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace");
        if (string.IsNullOrWhiteSpace(ns))
        {
            Logger.LogError("SendCapabilityOffer: missing namespace (config.Namespace/Namespace)");
            return NodeStatus.Failure;
        }

        var moduleId = Context.Get<string>("ModuleId") ?? Context.Get<string>("config.Agent.ModuleId");
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            Logger.LogError("SendCapabilityOffer: missing module id (ModuleId/config.Agent.ModuleId)");
            return NodeStatus.Failure;
        }

        var topic = $"/{ns}/{moduleId}/PlanningAgent/OfferResponse";

        // IMPORTANT: The offer sequence is modeled explicitly (OfferedCapabilitySequence).
        // Do not embed nested sequences inside OfferedCapability.
        var offeredSequence = new ManufacturingOfferedCapabilitySequence();

        var pre = plan.SupplementalCapabilities
            .Where(c => c != null && !IsPostPlacement(c))
            .ToList();
        var post = plan.SupplementalCapabilities
            .Where(c => c != null && IsPostPlacement(c))
            .ToList();

        foreach (var cap in pre)
        {
            offeredSequence.AddCapability(cap);
        }

        offeredSequence.AddCapability(offeredCapability);

        foreach (var cap in post)
        {
            offeredSequence.AddCapability(cap);
        }

        var proposal = new CapabilityOfferProposalMessage(
            capability: request.Capability,
            requirementId: request.RequirementId,
            offerId: plan.OfferId,
            station: plan.StationId,
            earliestStartUtc: plan.StartTimeUtc,
            cycleTime: plan.CycleTime,
            setupTime: plan.SetupTime,
            cost: plan.Cost,
            offeredCapabilitySequence: offeredSequence,
            productId: request.ProductId);

        await proposal.PublishAsync(
            client,
            topic,
            senderId: Context.AgentId,
            senderRole: string.IsNullOrWhiteSpace(Context.AgentRole) ? "PlanningAgent" : Context.AgentRole,
            receiverId: request.RequesterId,
            receiverRole: null,
            conversationId: request.ConversationId).ConfigureAwait(false);

        Logger.LogInformation("SendCapabilityOffer: sent proposal {OfferId} for requirement {Requirement}", plan.OfferId, request.RequirementId);
        return NodeStatus.Success;
    }

    private static bool IsPostPlacement(OfferedCapability capability)
    {
        var placement = capability?.SequencePlacement?.GetText();
        if (string.IsNullOrWhiteSpace(placement))
        {
            return false;
        }

        return placement.Equals("post", StringComparison.OrdinalIgnoreCase)
               || placement.Equals("after", StringComparison.OrdinalIgnoreCase)
               || placement.Equals("afterCapability", StringComparison.OrdinalIgnoreCase);
    }

}

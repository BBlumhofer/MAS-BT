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
using MAS_BT.Nodes.Planning;
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

        // Send response directly to the requester (dispatcher) using generic topic pattern
        var topic = $"/{ns}/{request.RequesterId}/OfferedCapability/Response";

        Logger.LogInformation(
            "SendCapabilityOffer: sending proposal to RequesterId={RequesterId} Topic={Topic}",
            request.RequesterId,
            topic);

        // IMPORTANT: The offer sequence is modeled explicitly (OfferedCapabilitySequence).
        // Do not embed nested sequences inside OfferedCapability.
        var offeredSequence = new ManufacturingOfferedCapabilitySequence();

        var pre = plan.SupplementalCapabilities
            .Where(c => c != null && !IsPostPlacement(c))
            .ToList();
        var post = plan.SupplementalCapabilities
            .Where(c => c != null && IsPostPlacement(c))
            .ToList();

        // Also include any transport offers that may still be in the context (defensive: PlanCapabilityOfferNode may or may not have consumed them).
        var transportSequence = Context.Get<System.Collections.Generic.List<MAS_BT.Nodes.Planning.ProcessChain.TransportSequenceItem>>("Planning.TransportSequence");
        if (transportSequence != null && transportSequence.Count > 0)
        {
            foreach (var entry in transportSequence)
            {
                if (entry?.Capability == null) continue;
                var cap = entry.Capability;
                cap.SetSequencePlacement(entry.Placement == MAS_BT.Nodes.Planning.ProcessChain.TransportPlacement.AfterCapability ? "post" : "pre");
                if (!IsPostPlacement(cap)) pre.Add(cap);
                else post.Add(cap);
            }
        }

        var transportOffers = Context.Get<System.Collections.Generic.List<AasSharpClient.Models.ProcessChain.OfferedCapability>>("Planning.TransportOffers");
        if (transportOffers != null && transportOffers.Count > 0)
        {
            foreach (var cap in transportOffers)
            {
                if (cap == null) continue;
                // respect declared placement if any, otherwise treat as pre
                if (IsPostPlacement(cap)) post.Add(cap);
                else pre.Add(cap);
            }
        }

        foreach (var cap in pre)
        {
            offeredSequence.AddCapability(cap);
        }

        offeredSequence.AddCapability(offeredCapability);

        foreach (var cap in post)
        {
            offeredSequence.AddCapability(cap);
        }

        // Diagnostic: log payload shape before publishing
        try
        {
            var caps = offeredSequence.GetCapabilities().ToList();
            Logger.LogDebug("SendCapabilityOffer: offered sequence contains {Count} capabilities", caps.Count);
            try { Console.WriteLine($"[DEBUG] SendCapabilityOffer: offered sequence contains {caps.Count} capabilities"); } catch {}
            for (var i = 0; i < caps.Count; i++)
            {
                var c = caps[i];
                var instance = c.InstanceIdentifier.GetText();
                var actions = c.Actions;
                var actionArray = actions != null ? actions.ToArray() : System.Array.Empty<ISubmodelElement>();
                Logger.LogDebug("SendCapabilityOffer: capability #{Index} id={Instance} actions={Count}", i + 1, instance, actionArray.Length);
                try { Console.WriteLine($"[DEBUG] SendCapabilityOffer: capability #{i+1} id={instance} actions={actionArray.Length}"); } catch {}
                foreach (var ae in actionArray)
                {
                    try
                    {
                        var typeName = ae.GetType().FullName ?? ae.GetType().Name;
                        if (ae is AasSharpClient.Models.Action am)
                        {
                            var pcount = am.InputParameters?.Parameters?.Count ?? 0;
                            Logger.LogDebug("SendCapabilityOffer: action {Title} typed InputParameters={ParamCount}", am.ActionTitle, pcount);
                            Console.WriteLine($"[DEBUG] SendCapabilityOffer: action (typed) Title={am.ActionTitle} InputParametersCount={pcount} Type={typeName}");
                        }
                        else if (ae is BaSyx.Models.AdminShell.SubmodelElementCollection coll)
                        {
                            var hasIp = (coll.Values ?? System.Array.Empty<BaSyx.Models.AdminShell.ISubmodelElement>())
                                .OfType<BaSyx.Models.AdminShell.SubmodelElementCollection>()
                                .Any(smc => string.Equals(smc.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase));
                            Logger.LogDebug("SendCapabilityOffer: action collection idShort={IdShort} hasInputParameters={HasIp}", coll.IdShort, hasIp);
                            Console.WriteLine($"[DEBUG] SendCapabilityOffer: action collection IdShort={coll.IdShort} hasInputParameters={hasIp} Type={typeName}");
                            // enumerate children for more context
                            try
                            {
                                foreach (var child in coll.Values ?? System.Array.Empty<ISubmodelElement>())
                                {
                                    var childType = child.GetType().FullName ?? child.GetType().Name;
                                    Console.WriteLine($"[DEBUG] SendCapabilityOffer:   child IdShort={child.IdShort} Type={childType}");
                                    if (child is BaSyx.Models.AdminShell.SubmodelElementCollection childCol && string.Equals(childCol.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase))
                                    {
                                        foreach (var p in childCol.OfType<Property>())
                                        {
                                            Console.WriteLine($"[DEBUG] SendCapabilityOffer:     InputParameter {p.IdShort} = {p.GetText()}");
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (ae is Property prop)
                        {
                            Console.WriteLine($"[DEBUG] SendCapabilityOffer: action element is Property IdShort={prop.IdShort} Value={prop.GetText()} Type={typeName}");
                        }
                        else
                        {
                            Console.WriteLine($"[DEBUG] SendCapabilityOffer: action element unknown Type={typeName} IdShort={ae.IdShort}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "SendCapabilityOffer: failed to inspect action element");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "SendCapabilityOffer: diagnostics logging failed");
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

        // Clear the current message to prevent reprocessing after sending offer
        Context.Set("LastReceivedMessage", (I40Sharp.Messaging.Models.I40Message?)null);
        Context.Set("CurrentMessage", (I40Sharp.Messaging.Models.I40Message?)null);

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

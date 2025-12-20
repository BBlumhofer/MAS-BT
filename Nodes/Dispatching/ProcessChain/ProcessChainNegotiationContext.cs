using System;
using System.Collections.Generic;
using System.Linq;
using AasSharpClient.Models.ProcessChain;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

/// <summary>
/// Holds all temporary state for a single ProcessChain negotiation round.
/// </summary>
public class ProcessChainNegotiationContext
{
    public string ConversationId { get; set; } = string.Empty;
    public string RequesterId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public SubmodelElementCollection? RequestProcessChainElement { get; set; }
    public SubmodelElementCollection? AssetLocation { get; set; }
    public CapabilityRequirementCollection Requirements { get; } = new CapabilityRequirementCollection();

    public bool HasCompleteProcessChain => Requirements.All(r => r.CapabilityOffers.Count > 0 || r.OfferedCapabilitySequences.Count > 0);
}

public class CapabilityRequirement
{
    public string Capability { get; set; } = string.Empty;
    public string RequirementId { get; set; } = Guid.NewGuid().ToString();
    public CapabilityContainer? CapabilityContainer { get; set; }
    public IList<OfferedCapability> CapabilityOffers { get; } = new List<OfferedCapability>();
    public IList<ManufacturingOfferedCapabilitySequence> OfferedCapabilitySequences { get; } = new List<ManufacturingOfferedCapabilitySequence>();
    public string? RequestedInstanceIdentifier { get; set; }
    public Reference? RequestedCapabilityReference { get; set; }
    public SubmodelElementCollection? RequestedCapabilityElement { get; set; }

    public void AddOffer(OfferedCapability offer)
    {
        if (offer != null)
        {
            CapabilityOffers.Add(offer);
        }
    }

    public void AddSequence(ManufacturingOfferedCapabilitySequence sequence)
    {
        if (sequence != null)
        {
            OfferedCapabilitySequences.Add(sequence);
        }
    }
}

public class CapabilityRequirementCollection : List<CapabilityRequirement>
{
    public CapabilityRequirement Add(CapabilityContainer container)
    {
        if (container == null)
        {
            throw new ArgumentNullException(nameof(container));
        }

        var requirement = new CapabilityRequirement
        {
            Capability = container.GetCapabilityName(),
            CapabilityContainer = container
        };

        base.Add(requirement);
        return requirement;
    }

    public CapabilityRequirement Add(string capabilityName)
    {
        var requirement = new CapabilityRequirement
        {
            Capability = string.IsNullOrWhiteSpace(capabilityName) ? "Capability" : capabilityName
        };

        base.Add(requirement);
        return requirement;
    }

    public new void Add(CapabilityRequirement requirement)
    {
        if (requirement == null)
        {
            throw new ArgumentNullException(nameof(requirement));
        }

        base.Add(requirement);
    }
}

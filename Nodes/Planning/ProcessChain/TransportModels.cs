using System;
using System.Collections.Generic;
using AasSharpClient.Models.ProcessChain;

namespace MAS_BT.Nodes.Planning.ProcessChain;

/// <summary>
/// Indicates whether a supplemental transport must happen before or after the main capability.
/// </summary>
public enum TransportPlacement
{
    BeforeCapability,
    AfterCapability
}

/// <summary>
/// Represents a detected transport requirement (typically derived from storage pre/post conditions).
/// </summary>
public sealed class TransportRequirement
{
    public string Target { get; set; } = string.Empty;
    public TransportPlacement Placement { get; set; } = TransportPlacement.BeforeCapability;
    public string? SourceId { get; set; }
    // Optional placeholder coming from the StorageConstraint (e.g. "*" or a literal ProductId)
    public string? ProductIdPlaceholder { get; set; }
    // Optional resolved product id after binding
    public string? ResolvedProductId { get; set; }
}

/// <summary>
/// Captures a transport capability that should be injected into the ManufacturingSequence.
/// </summary>
public sealed class TransportSequenceItem
{
    public TransportSequenceItem(TransportPlacement placement, OfferedCapability capability)
    {
        Placement = placement;
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
    }

    public TransportPlacement Placement { get; }
    public OfferedCapability Capability { get; }
}

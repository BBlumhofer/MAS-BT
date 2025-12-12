using System;

namespace MAS_BT.Nodes.Planning.ProcessChain;

/// <summary>
/// Holds parsed data from a capability call-for-proposal.
/// </summary>
public class CapabilityRequestContext
{
    public string ConversationId { get; set; } = string.Empty;
    public string RequirementId { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string RequesterId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
}

/// <summary>
/// Planned response metadata that will be serialized into the proposal message.
/// </summary>
public class CapabilityOfferPlan
{
    public string OfferId { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan SetupTime { get; set; } = TimeSpan.Zero;
    public TimeSpan CycleTime { get; set; } = TimeSpan.FromMinutes(1);
    public double Cost { get; set; } = 0.0;
}

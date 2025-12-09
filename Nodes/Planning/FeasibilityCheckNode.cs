using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// FeasibilityCheck - uses config defaults if present; otherwise computes dummy cost/time.
/// On failure, sets RefusalReason and returns Failure.
/// </summary>
public class FeasibilityCheckNode : BTNode
{
    public double DefaultCost { get; set; } = 10.0;
    public double DefaultDurationSec { get; set; } = 120;
    public string RefusalReason { get; set; } = "feasibility_failed";

    public FeasibilityCheckNode() : base("FeasibilityCheck") {}

    public override Task<NodeStatus> Execute()
    {
        var capability = Context.Get<string>("DerivedCapability") ?? Context.Get<string>("MatchedCapability") ?? string.Empty;
        var forceFail = Context.Get<bool?>("ForceFeasibilityFail") ?? false;

        // read optional defaults from context dictionary
        var defaults = Context.Get<Dictionary<string, (double Cost, double DurationSec)?>>("FeasibilityDefaults");
        var defaultTuple = defaults != null && defaults.TryGetValue(capability, out var tuple) ? tuple : null;

        var cost = defaultTuple?.Cost ?? DefaultCost;
        var duration = defaultTuple?.DurationSec ?? DefaultDurationSec;

        if (forceFail || string.IsNullOrWhiteSpace(capability))
        {
            Logger.LogWarning("FeasibilityCheck: failing for capability={Capability}", capability);
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }

        var result = new { Capability = capability, Cost = cost, DurationSec = duration, Feasible = true };
        Context.Set("FeasibilityResult", result);
        Context.Set("FeasibleCost", cost);
        Context.Set("FeasibleDurationSec", duration);

        Logger.LogInformation("FeasibilityCheck: capability={Capability}, cost={Cost}, durationSec={Duration}", capability, cost, duration);
        return Task.FromResult(NodeStatus.Success);
    }
}

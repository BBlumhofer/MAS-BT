using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SchedulingFeedback - stub: enriches context with scheduling outputs like marginal cost and start time.
/// </summary>
public class SchedulingFeedbackNode : BTNode
{
    public SchedulingFeedbackNode() : base("SchedulingFeedback") {}

    public override Task<NodeStatus> Execute()
    {
        var marginal = Context.Get<double?>("SchedulingMarginalCost") ?? Context.Get<double?>("FeasibleCost") ?? 10.0;
        var startTime = Context.Get<string>("ScheduledStartTime") ?? System.DateTime.UtcNow.ToString("o");
        Context.Set("SchedulingMarginalCost", marginal);
        Context.Set("ScheduledStartTime", startTime);
        Logger.LogInformation("SchedulingFeedback: marginalCost={Cost} start={Start}", marginal, startTime);
        return Task.FromResult(NodeStatus.Success);
    }
}

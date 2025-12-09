using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// SchedulingExecute - stub: logs scheduling request and succeeds.
/// </summary>
public class SchedulingExecuteNode : BTNode
{
    public string Capability { get; set; } = string.Empty;
    public string RefusalReason { get; set; } = "scheduling_failed";

    public SchedulingExecuteNode() : base("SchedulingExecute") {}

    public override Task<NodeStatus> Execute()
    {
        var cap = ResolvePlaceholders(Capability);
        var forceFail = Context.Get<bool?>("ForceSchedulingFail") ?? false;
        if (forceFail || string.IsNullOrWhiteSpace(cap))
        {
            Logger.LogWarning("SchedulingExecute: scheduling failed for capability={Capability}", cap);
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }

        var feasibleCost = Context.Get<double?>("FeasibleCost") ?? 10.0;
        var marginal = feasibleCost * 1.1; // stub margin
        Context.Set("LastSchedulingCapability", cap);
        Context.Set("SchedulingMarginalCost", marginal);
        Logger.LogInformation("SchedulingExecute: scheduled capability={Capability} marginalCost={Cost}", cap, marginal);
        return Task.FromResult(NodeStatus.Success);
    }
}

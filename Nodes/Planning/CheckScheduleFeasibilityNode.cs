using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// CheckScheduleFeasibility - stub: validates schedule against current machine state flags in context.
/// </summary>
public class CheckScheduleFeasibilityNode : BTNode
{
    public string RefusalReason { get; set; } = "schedule_not_feasible";

    public CheckScheduleFeasibilityNode() : base("CheckScheduleFeasibility") {}

    public override Task<NodeStatus> Execute()
    {
        var machineReady = Context.Get<bool?>("MachineReady") ?? true;
        var resourceAvailable = Context.Get<bool?>("ResourceAvailable") ?? true;
        if (!machineReady || !resourceAvailable)
        {
            Logger.LogWarning("CheckScheduleFeasibility: machineReady={Ready}, resourceAvailable={Avail}", machineReady, resourceAvailable);
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }
        Logger.LogInformation("CheckScheduleFeasibility: feasible (machineReady={Ready}, resourceAvailable={Avail})", machineReady, resourceAvailable);
        return Task.FromResult(NodeStatus.Success);
    }
}

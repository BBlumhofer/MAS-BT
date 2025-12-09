using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// UpdateMachineSchedule - stub: logs schedule update request.
/// </summary>
public class UpdateMachineScheduleNode : BTNode
{
    public string ModuleId { get; set; } = string.Empty;

    public UpdateMachineScheduleNode() : base("UpdateMachineSchedule") {}

    public override Task<NodeStatus> Execute()
    {
        var moduleId = ResolvePlaceholders(ModuleId);
        var offer = Context.Get<object>("CurrentOffer");
        Logger.LogInformation("UpdateMachineSchedule: marking offer pending for {ModuleId}", moduleId);
        Context.Set("LastScheduleUpdateModule", moduleId);
        Context.Set("PendingOffer", offer);
        return Task.FromResult(NodeStatus.Success);
    }
}

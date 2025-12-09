using System.Linq;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// CheckConstraints - inspects DerivedStep/CurrentPlanAction for storage preconditions and flags transport needs.
/// </summary>
public class CheckConstraintsNode : BTNode
{
    public string RefusalReason { get; set; } = "constraints_failed";

    public CheckConstraintsNode() : base("CheckConstraints") {}

    public override Task<NodeStatus> Execute()
    {
        var action = Context.Get<ActionModel>("CurrentPlanAction");
        var step = Context.Get<Step>("DerivedStep") ?? Context.Get<Step>("CurrentPlanStep");
        if (action == null)
        {
            Logger.LogError("CheckConstraints: missing CurrentPlanAction");
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }

        var preconditions = action.Preconditions?.OfType<SubmodelElementCollection>() ?? Enumerable.Empty<SubmodelElementCollection>();
        var hasInStorage = preconditions.Any(pc => pc.IdShort.Contains("storage", System.StringComparison.OrdinalIgnoreCase) ||
                                                   pc.FirstOrDefault(el => el.IdShort is "ConditionType") is Property<string> p &&
                                                   (p.Value.Value?.ToString() ?? string.Empty).Contains("InStorage", System.StringComparison.OrdinalIgnoreCase));

        Context.Set("RequiresTransport", hasInStorage);
        if (hasInStorage)
        {
            // best effort: try to extract target station/value
            var storage = preconditions.First();
            var slotValue = storage.FirstOrDefault(el => el.IdShort is "SlotValue") as Property<string>;
            var target = slotValue?.Value.Value?.ToString() ?? Context.Get<string>("MachineName") ?? string.Empty;
            Context.Set("TransportTarget", target);
            Logger.LogInformation("CheckConstraints: InStorage constraint detected, transport target={Target}", target);
        }
        else
        {
            Logger.LogInformation("CheckConstraints: no InStorage constraints detected");
        }

        Context.Set("CurrentPlanStep", step);
        Context.Set("DerivedStep", step);
        return Task.FromResult(NodeStatus.Success);
    }
}

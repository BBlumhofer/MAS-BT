using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning.ProcessChain;
using Microsoft.Extensions.Logging;
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
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");

        if (action == null && request?.CapabilityContainer == null)
        {
            Logger.LogInformation("CheckConstraints: no CurrentPlanAction or capability container available, skipping constraint evaluation");
            Context.Set("RequiresTransport", false);
            return Task.FromResult(NodeStatus.Success);
        }

        string? target = null;
        bool hasInStorage;

        if (action != null)
        {
            hasInStorage = TryDetectStoragePrecondition(action, out target);
        }
        else
        {
            hasInStorage = TryDetectStorageConstraint(request!.CapabilityContainer!, out target);
        }

        Context.Set("RequiresTransport", hasInStorage);
        if (hasInStorage)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                var fallback = Context.Get<string>("MachineName");
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    target = fallback;
                }
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                Context.Set("TransportTarget", target);
            }

            Logger.LogInformation("CheckConstraints: InStorage constraint detected, transport target={Target}", target ?? "<unknown>");
        }
        else
        {
            Logger.LogInformation("CheckConstraints: no InStorage constraints detected");
        }

        Context.Set("CurrentPlanStep", step);
        Context.Set("DerivedStep", step);
        return Task.FromResult(NodeStatus.Success);
    }

    private static bool TryDetectStoragePrecondition(ActionModel action, out string? target)
    {
        target = null;
        var preconditions = action.Preconditions?.OfType<SubmodelElementCollection>() ?? Enumerable.Empty<SubmodelElementCollection>();
        foreach (var pc in preconditions)
        {
            if (pc.IdShort.Contains("storage", StringComparison.OrdinalIgnoreCase))
            {
                target = ExtractSlotValue(pc);
                return true;
            }

            var conditionType = pc.FirstOrDefault(el => el.IdShort is "ConditionType") as Property<string>;
            var conditionValue = conditionType?.Value.Value?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(conditionValue) &&
                conditionValue.Contains("InStorage", StringComparison.OrdinalIgnoreCase))
            {
                target = ExtractSlotValue(pc);
                return true;
            }
        }

        return false;
    }

    private static string? ExtractSlotValue(SubmodelElementCollection storage)
    {
        var slotValue = storage.FirstOrDefault(el => el.IdShort is "SlotValue") as Property<string>;
        return slotValue?.Value.Value?.ToString();
    }

    private static bool TryDetectStorageConstraint(CapabilityContainer container, out string? target)
    {
        target = null;
        foreach (var constraint in container.Constraints)
        {
            if (ConstraintIndicatesStorage(constraint, out target))
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = container.GetCapabilityName();
                }
                return true;
            }
        }

        return false;
    }

    private static bool ConstraintIndicatesStorage(PropertyConstraintContainerSection constraint, out string? target)
    {
        target = null;

        if (!ContainsStorageKeyword(constraint.Source.IdShort) &&
            !ContainsStorageKeyword(constraint.ConstraintType?.Value?.Value?.ToString()) &&
            !ContainsStorageKeyword(constraint.ConditionalType?.Value?.Value?.ToString()) &&
            !ConstraintCustomSectionContainsStorage(constraint, out target))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            target = constraint.CustomConstraint?.GetProperty("SlotValue")?.Value?.Value?.ToString()
                     ?? constraint.CustomConstraint?.GetProperty("TargetStation")?.Value?.Value?.ToString();
        }

        return true;
    }

    private static bool ConstraintCustomSectionContainsStorage(PropertyConstraintContainerSection constraint, out string? target)
    {
        target = null;
        var custom = constraint.CustomConstraint;
        if (custom == null)
        {
            return false;
        }

        foreach (var prop in custom.Properties)
        {
            if (ContainsStorageKeyword(prop.IdShort) || ContainsStorageKeyword(prop.Value?.Value?.ToString()))
            {
                target = prop.Value?.Value?.ToString();
                return true;
            }
        }

        return false;
    }

    private static bool ContainsStorageKeyword(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains("storage", StringComparison.OrdinalIgnoreCase);
    }
}

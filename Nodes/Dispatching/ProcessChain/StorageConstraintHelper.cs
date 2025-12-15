using System;
using System.Collections.Generic;
using System.Linq;
using AasSharpClient.Models;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

internal static class StorageConstraintHelper
{
    internal sealed record ConstraintTarget(CapabilityRequirement Requirement, string TargetStation);

    public static List<ConstraintTarget> FindStorageTargets(ProcessChainNegotiationContext negotiation)
    {
        var results = new List<ConstraintTarget>();
        foreach (var requirement in negotiation.Requirements)
        {
            var targets = ExtractTargetsFromOffers(requirement);
            if (targets.Count == 0)
            {
                targets = ExtractTargetsFromConstraints(requirement.CapabilityContainer);
            }

            foreach (var target in targets)
            {
                results.Add(new ConstraintTarget(requirement, target));
            }
        }

        return results;
    }

    private static List<string> ExtractTargetsFromOffers(CapabilityRequirement requirement)
    {
        var targets = new List<string>();
        foreach (var offer in requirement.CapabilityOffers)
        {
            foreach (var action in offer.Actions.OfType<AasSharpClient.Models.Action>())
            {
                var preconditions = action.Preconditions?.Values?.OfType<SubmodelElementCollection>() ?? Enumerable.Empty<SubmodelElementCollection>();
                foreach (var precondition in preconditions)
                {
                    if (IndicatesStorage(precondition, out var target))
                    {
                        targets.Add(target ?? offer.Station?.Value?.Value?.ToString() ?? string.Empty);
                    }
                }
            }
        }

        return targets.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractTargetsFromConstraints(CapabilityContainer? container)
    {
        var targets = new List<string>();
        if (container?.Constraints == null)
        {
            return targets;
        }

        foreach (var constraint in container.Constraints)
        {
            if (ConstraintIndicatesStorage(constraint, out var target))
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = container.GetCapabilityName();
                }

                if (!string.IsNullOrWhiteSpace(target))
                {
                    targets.Add(target);
                }
            }
        }

        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IndicatesStorage(SubmodelElementCollection precondition, out string? target)
    {
        target = null;
        if (precondition == null)
        {
            return false;
        }

        var typeProperty = precondition.FirstOrDefault(e => e.IdShort.Equals("ConditionType", StringComparison.OrdinalIgnoreCase)) as Property<string>;
        if (typeProperty?.Value?.Value is string typeValue &&
            typeValue.Contains("InStorage", StringComparison.OrdinalIgnoreCase))
        {
            target = ExtractSlotValue(precondition);
            return true;
        }

        if (precondition.IdShort.Contains("storage", StringComparison.OrdinalIgnoreCase))
        {
            target = ExtractSlotValue(precondition);
            return true;
        }

        return false;
    }

    private static string? ExtractSlotValue(SubmodelElementCollection storage)
    {
        var slotValue = storage.FirstOrDefault(e => e.IdShort.Equals("SlotValue", StringComparison.OrdinalIgnoreCase)) as Property<string>;
        return slotValue?.Value?.Value?.ToString();
    }

    private static bool ConstraintIndicatesStorage(PropertyConstraintContainerSection constraint, out string? target)
    {
        target = null;
        if (constraint == null)
        {
            return false;
        }

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
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

        var requirements = new List<TransportRequirement>();
        if (action != null)
        {
            requirements.AddRange(ExtractTransportRequirements(action.Preconditions, TransportPlacement.BeforeCapability));
            requirements.AddRange(ExtractTransportRequirements(action.Effects, TransportPlacement.AfterCapability));
        }

        if (requirements.Count == 0 && request?.CapabilityContainer != null)
        {
            if (TryDetectStorageConstraint(request.CapabilityContainer, out var constraintTarget, out var constraintProductPlaceholder))
            {
                requirements.Add(new TransportRequirement
                {
                    Target = string.IsNullOrWhiteSpace(constraintTarget) ? (request.Capability ?? string.Empty) : constraintTarget!,
                    Placement = TransportPlacement.BeforeCapability,
                    SourceId = request.RequestedInstanceIdentifier ?? request.RequirementId,
                    ProductIdPlaceholder = string.IsNullOrWhiteSpace(constraintProductPlaceholder) ? null : constraintProductPlaceholder
                });
            }
        }

        var requiresTransport = requirements.Count > 0;
        Context.Set("RequiresTransport", requiresTransport);
        Context.Set("Planning.TransportRequirements", requirements);

        if (requiresTransport)
        {
            var firstTarget = requirements.FirstOrDefault()?.Target;
            if (string.IsNullOrWhiteSpace(firstTarget))
            {
                firstTarget = Context.Get<string>("MachineName");
            }

            if (!string.IsNullOrWhiteSpace(firstTarget))
            {
                Context.Set("TransportTarget", firstTarget);
            }

            Logger.LogInformation(
                "CheckConstraints: detected {Count} transport requirement(s): {Details}",
                requirements.Count,
                string.Join(", ", requirements.Select(r => $"{r.Placement}:{r.Target ?? "<unknown>"}")));
        }
        else
        {
            Context.Set("TransportTarget", null);
            Logger.LogInformation("CheckConstraints: no transport constraints detected");
        }

        Context.Set("CurrentPlanStep", step);
        Context.Set("DerivedStep", step);
        return Task.FromResult(NodeStatus.Success);
    }

    private static IEnumerable<TransportRequirement> ExtractTransportRequirements(SubmodelElementCollection? parent, TransportPlacement placement)
    {
        if (parent == null)
        {
            yield break;
        }

        foreach (var collection in parent.OfType<SubmodelElementCollection>())
        {
            if (!LooksLikeStorageCondition(collection))
            {
                continue;
            }

            var target = ExtractStorageTarget(collection);
            var req = new TransportRequirement
            {
                Placement = placement,
                Target = target ?? string.Empty,
                SourceId = collection.IdShort
            };

            // Capture ProductId placeholder if present in the constraint (e.g., "*" or a concrete id)
            var pid = FindPropertyValue(collection, "ProductId") ?? FindPropertyValue(collection, "ProductID");
            if (!string.IsNullOrWhiteSpace(pid))
            {
                req.ProductIdPlaceholder = pid;
            }

            yield return req;
        }
    }

    private static string? ExtractStorageTarget(SubmodelElementCollection collection)
    {
        var slotValue = FindPropertyValue(collection, "SlotValue");
        if (!string.IsNullOrWhiteSpace(slotValue))
        {
            return slotValue;
        }

        var targetStation = FindPropertyValue(collection, "TargetStation");
        if (!string.IsNullOrWhiteSpace(targetStation))
        {
            return targetStation;
        }

        var machineName = FindPropertyValue(collection, "MachineName");
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            return machineName;
        }

        return null;
    }

    private static bool TryDetectStorageConstraint(CapabilityContainer container, out string? target, out string? productIdPlaceholder)
    {
        target = null;
        productIdPlaceholder = null;
        foreach (var constraint in container.Constraints)
        {
            if (ConstraintIndicatesStorage(constraint, out target))
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = container.GetCapabilityName();
                }

                // try to read ProductId/ProductID from the CustomConstraint section if present
                try
                {
                    var custom = constraint.CustomConstraint;
                    if (custom != null && custom.Properties != null)
                    {
                        var prop = custom.Properties.FirstOrDefault(p => string.Equals(p.IdShort, "ProductId", StringComparison.OrdinalIgnoreCase)
                                                                        || string.Equals(p.IdShort, "ProductID", StringComparison.OrdinalIgnoreCase));
                        var pid = prop?.GetText();
                        if (!string.IsNullOrWhiteSpace(pid))
                        {
                            productIdPlaceholder = pid;
                        }
                    }
                }
                catch { /* best-effort */ }

                return true;
            }
        }

        return false;
    }

    private static bool ConstraintIndicatesStorage(PropertyConstraintContainerSection constraint, out string? target)
    {
        target = null;

        if (!ContainsStorageKeyword(constraint.Source.IdShort) &&
            !ContainsStorageKeyword(constraint.ConstraintType?.GetText()) &&
            !ContainsStorageKeyword(constraint.ConditionalType?.GetText()) &&
            !ConstraintCustomSectionContainsStorage(constraint, out target))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            target = constraint.CustomConstraint?.GetProperty("SlotValue")?.GetText()
                     ?? constraint.CustomConstraint?.GetProperty("TargetStation")?.GetText();
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
            if (ContainsStorageKeyword(prop.IdShort) || ContainsStorageKeyword(prop.GetText()))
            {
                target = prop.GetText();
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

    private static string? ExtractSlotValue(SubmodelElementCollection storage)
    {
        return FindPropertyValue(storage, "SlotValue");
    }

    private static bool LooksLikeStorageCondition(SubmodelElementCollection? collection)
    {
        if (collection == null)
        {
            return false;
        }

        if (ContainsStorageKeyword(collection.IdShort))
        {
            return true;
        }

        foreach (var element in EnumerateDescendants(collection))
        {
            if (element is Property property)
            {
                if (ContainsStorageKeyword(property.IdShort) ||
                    ContainsStorageKeyword(property.GetText()))
                {
                    return true;
                }
            }
            else if (element is SubmodelElementCollection nested && ContainsStorageKeyword(nested.IdShort))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ISubmodelElement> EnumerateDescendants(SubmodelElementCollection collection)
    {
        if (collection?.Values == null)
        {
            yield break;
        }

        foreach (var element in collection.Values)
        {
            if (element == null)
            {
                continue;
            }

            yield return element;

            if (element is SubmodelElementCollection nested)
            {
                foreach (var child in EnumerateDescendants(nested))
                {
                    yield return child;
                }
            }
        }
    }

    private static string? FindPropertyValue(SubmodelElementCollection? collection, string idShort)
    {
        if (collection?.Values == null)
        {
            return null;
        }

        foreach (var element in collection.Values)
        {
            if (element is Property prop && string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
            {
                return prop.GetText();
            }

            if (element is SubmodelElementCollection nested)
            {
                var nestedValue = FindPropertyValue(nested, idShort);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }
}

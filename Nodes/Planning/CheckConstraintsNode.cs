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
using UAClient.Client;
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

        // When constraints are not present in the incoming request (common for CfP), fall back to the local
        // capability description (loaded from this module's AAS) so we can still derive transport requirements
        // before matchmaking enriches constraints from Neo4j.
        if (requirements.Count == 0 && request != null)
        {
            var localContainer = ResolveLocalCapabilityContainer(request.Capability);
            if (localContainer != null && TryDetectStorageConstraint(localContainer, out var localTarget, out var localProductPlaceholder))
            {
                requirements.Add(new TransportRequirement
                {
                    Target = string.IsNullOrWhiteSpace(localTarget) ? (request.Capability ?? string.Empty) : localTarget!,
                    Placement = TransportPlacement.BeforeCapability,
                    SourceId = request.RequestedInstanceIdentifier ?? request.RequirementId,
                    ProductIdPlaceholder = string.IsNullOrWhiteSpace(localProductPlaceholder) ? null : localProductPlaceholder
                });
            }
        }

        // Resolve wildcard placeholders ("*") early so downstream nodes (matchmaking/offer/manufacturing-sequence)
        // work on resolved values.
        ResolveStorageConstraintPlaceholders(request, action, requirements);

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

    private void ResolveStorageConstraintPlaceholders(CapabilityRequestContext? request, ActionModel? action, IList<TransportRequirement> requirements)
    {
        if (requirements == null || requirements.Count == 0)
        {
            return;
        }

        // Resolve per requirement (prefer explicit request.ProductId, then inventory, then remote server).
        foreach (var requirement in requirements)
        {
            if (requirement == null)
            {
                continue;
            }

            var placeholder = requirement.ProductIdPlaceholder?.Trim();
            if (string.IsNullOrWhiteSpace(placeholder))
            {
                continue;
            }

            if (!string.Equals(placeholder, "*", StringComparison.Ordinal))
            {
                // literal already provided
                requirement.ResolvedProductId = placeholder;
                continue;
            }

            var resolved = ResolveProductIdForTarget(request, requirement.Target);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                Logger.LogDebug("CheckConstraints: cannot resolve ProductId placeholder '*' for target={Target}", requirement.Target);
                continue;
            }

            requirement.ResolvedProductId = resolved;
            // overwrite placeholder so downstream consumers that don't look at ResolvedProductId still see resolved value
            requirement.ProductIdPlaceholder = resolved;

            // Mutate storage-constraint properties where possible (best-effort)
            try
            {
                ReplaceProductIdInRequestConstraints(request, requirement.Target, resolved);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "CheckConstraints: failed to rewrite ProductId in request constraints");
            }

            try
            {
                ReplaceProductIdInActionCollections(action, requirement.Target, resolved);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "CheckConstraints: failed to rewrite ProductId in action collections");
            }

            // IMPORTANT: PlanCapabilityOffer clones pre-/post-constraints from the local capability description.
            // Therefore we also rewrite placeholders in the local container so offers/manufacturing sequences
            // contain resolved IDs instead of "*".
            try
            {
                ReplaceProductIdInLocalCapabilityDescription(request?.Capability, requirement.Target, resolved);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "CheckConstraints: failed to rewrite ProductId in local capability description");
            }
        }

        // If the request doesn't have a ProductId yet and we resolved exactly one unique id, persist it.
        if (request != null && string.IsNullOrWhiteSpace(request.ProductId))
        {
            var unique = requirements
                .Select(r => r?.ResolvedProductId)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unique.Count == 1)
            {
                request.ProductId = unique[0] ?? string.Empty;
                Context.Set("Planning.CapabilityRequest", request);
                Logger.LogDebug("CheckConstraints: set request.ProductId={ProductId} from resolved storage constraints", request.ProductId);
            }
        }
    }

    private string? ResolveProductIdForTarget(CapabilityRequestContext? request, string? target)
    {
        // 1) explicit in request
        if (!string.IsNullOrWhiteSpace(request?.ProductId))
        {
            return request!.ProductId;
        }

        // 2) ModuleInventory snapshot in context
        var storageUnits = Context.Get<List<StorageUnit>>("ModuleInventory");
        if (storageUnits != null && storageUnits.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(target))
            {
                var match = storageUnits.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
                if (match?.Slots != null)
                {
                    var slot = match.Slots.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Content?.ProductID));
                    if (!string.IsNullOrWhiteSpace(slot?.Content?.ProductID))
                    {
                        return slot!.Content!.ProductID;
                    }
                }
            }

            foreach (var su in storageUnits)
            {
                var slot = su.Slots?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Content?.ProductID));
                if (!string.IsNullOrWhiteSpace(slot?.Content?.ProductID))
                {
                    return slot!.Content!.ProductID;
                }
            }
        }

        // 3) RemoteServer model (best-effort)
        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server?.Modules == null)
            {
                return null;
            }

            foreach (var modKv in server.Modules)
            {
                var storages = modKv.Value?.Storages;
                if (storages == null) continue;

                foreach (var st in storages)
                {
                    var sKey = st.Key;
                    var sVal = st.Value;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        if (!string.Equals(sKey, target, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(sVal?.Name, target, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (sVal?.Slots == null) continue;
                    foreach (var slot in sVal.Slots.Values)
                    {
                        var pid = slot?.ProductId;
                        if (!string.IsNullOrWhiteSpace(pid))
                        {
                            return pid;
                        }
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }

        return null;
    }

    private static void ReplaceProductIdInRequestConstraints(CapabilityRequestContext? request, string? target, string resolvedProductId)
    {
        if (request?.CapabilityContainer?.Constraints == null)
        {
            return;
        }

        foreach (var constraint in request.CapabilityContainer.Constraints)
        {
            if (!ConstraintIndicatesStorage(constraint, out var detectedTarget))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target) &&
                !string.IsNullOrWhiteSpace(detectedTarget) &&
                !string.Equals(detectedTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var custom = constraint.CustomConstraint;
            if (custom?.Properties == null) continue;

            foreach (var prop in custom.Properties.OfType<Property>())
            {
                if (!string.Equals(prop.IdShort, "ProductId", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(prop.IdShort, "ProductID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var current = prop.GetText();
                if (string.Equals(current?.Trim(), "*", StringComparison.Ordinal))
                {
                    prop.Value = new PropertyValue<string>(resolvedProductId);
                }
            }
        }
    }

    private static void ReplaceProductIdInActionCollections(ActionModel? action, string? target, string resolvedProductId)
    {
        if (action == null)
        {
            return;
        }

        ReplaceProductIdInStorageCollections(action.Preconditions, target, resolvedProductId);
        ReplaceProductIdInStorageCollections(action.Effects, target, resolvedProductId);
    }

    private void ReplaceProductIdInLocalCapabilityDescription(string? capabilityName, string? target, string resolvedProductId)
    {
        var local = ResolveLocalCapabilityContainer(capabilityName);
        if (local == null)
        {
            return;
        }

        foreach (var constraint in local.Constraints)
        {
            if (!ConstraintIndicatesStorage(constraint, out var detectedTarget))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target) &&
                !string.IsNullOrWhiteSpace(detectedTarget) &&
                !string.Equals(detectedTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var custom = constraint.CustomConstraint;
            if (custom?.Properties == null) continue;

            foreach (var prop in custom.Properties.OfType<Property>())
            {
                if (!string.Equals(prop.IdShort, "ProductId", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(prop.IdShort, "ProductID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var current = prop.GetText();
                if (string.Equals(current?.Trim(), "*", StringComparison.Ordinal))
                {
                    prop.Value = new PropertyValue<string>(resolvedProductId);
                }
            }
        }
    }

    private static void ReplaceProductIdInStorageCollections(SubmodelElementCollection? parent, string? target, string resolvedProductId)
    {
        if (parent == null)
        {
            return;
        }

        foreach (var collection in parent.OfType<SubmodelElementCollection>())
        {
            if (!LooksLikeStorageCondition(collection))
            {
                continue;
            }

            var detectedTarget = ExtractStorageTarget(collection);
            if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(detectedTarget) &&
                !string.Equals(detectedTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var element in EnumerateDescendants(collection))
            {
                if (element is not Property prop)
                {
                    continue;
                }

                if (!string.Equals(prop.IdShort, "ProductId", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(prop.IdShort, "ProductID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var current = prop.GetText();
                if (string.Equals(current?.Trim(), "*", StringComparison.Ordinal))
                {
                    prop.Value = new PropertyValue<string>(resolvedProductId);
                }
            }
        }
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

    private CapabilityContainer? ResolveLocalCapabilityContainer(string? capabilityName)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return null;
        }

        var submodel = Context.Get<CapabilityDescriptionSubmodel>("CapabilityDescriptionSubmodel")
                      ?? Context.Get<CapabilityDescriptionSubmodel>("AAS.Submodel.CapabilityDescription");
        if (submodel?.CapabilitySet == null)
        {
            return null;
        }

        return submodel.CapabilitySet
            .OfType<SubmodelElementCollection>()
            .Select(collection => new CapabilityContainer(collection))
            .FirstOrDefault(container => string.Equals(container.GetCapabilityName(), capabilityName, StringComparison.OrdinalIgnoreCase));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Services.Graph;
using Microsoft.Extensions.Logging;
using ActionModel = AasSharpClient.Models.Action;
using RangeElement = BaSyx.Models.AdminShell.Range;
using AasSubmodelElementFactory = AasSharpClient.Models.SubmodelElementFactory;

namespace MAS_BT.Nodes.Planning.ProcessChain;

public class PlanCapabilityOfferNode : BTNode
{
    public double BaseCost { get; set; } = 50.0;
    public double CostPerMinute { get; set; } = 2.0;

    public PlanCapabilityOfferNode() : base("PlanCapabilityOffer") { }

    public override async Task<NodeStatus> Execute()
    {
        var request = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        if (request == null)
        {
            Logger.LogError("PlanCapabilityOffer: capability request missing from context");
            return NodeStatus.Failure;
        }

        var now = DateTime.UtcNow;
        var setup = TimeSpan.FromMinutes(1);
        var cycle = TimeSpan.FromMinutes(5);
        var plannedStart = now.AddMinutes(2);

        var station = Context.Get<string>("config.Agent.ModuleId")
                      ?? Context.Get<string>("ModuleId");
        if (string.IsNullOrWhiteSpace(station))
        {
            Logger.LogError("PlanCapabilityOffer: missing station/module id (config.Agent.ModuleId/ModuleId)");
            return NodeStatus.Failure;
        }

        var cost = BaseCost + (cycle.TotalMinutes * CostPerMinute);
        if (!string.IsNullOrEmpty(request.ProductId))
        {
            cost += request.ProductId.Length;
        }

        var rawOfferId = $"{station}-{Guid.NewGuid():N}";
        var offerId = rawOfferId.Length > 40 ? rawOfferId[..40] : rawOfferId;

        var plan = new CapabilityOfferPlan
        {
            OfferId = offerId,
            StationId = station,
            StartTimeUtc = plannedStart,
            SetupTime = setup,
            CycleTime = cycle,
            Cost = Math.Round(cost, 2)
        };

        var offeredCapability = new OfferedCapability("OfferedCapability");
        offeredCapability.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
        offeredCapability.Station.Value = new PropertyValue<string>(station);
        offeredCapability.MatchingScore.Value = new PropertyValue<double>(1.0);
        offeredCapability.SetEarliestScheduling(plannedStart, plannedStart.Add(cycle), setup, cycle);
        offeredCapability.SetCost(plan.Cost);

        if (!string.IsNullOrWhiteSpace(request.Capability))
        {
            var moduleId = Context.Get<string>("config.Agent.ModuleId")
                ?? Context.Get<string>("ModuleId");

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                Logger.LogError("PlanCapabilityOffer: missing module id (config.Agent.ModuleId/ModuleId) for capability reference resolution");
                return NodeStatus.Failure;
            }

            var referenceQuery = Context.Get<ICapabilityReferenceQuery>("CapabilityReferenceQuery");
            if (referenceQuery == null)
            {
                Logger.LogError("PlanCapabilityOffer: CapabilityReferenceQuery missing");
                return NodeStatus.Failure;
            }

            var json = await referenceQuery.GetCapabilityReferenceJsonAsync(moduleId, request.Capability).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || !TryParseNeo4jReferenceJson(json!, out var modelRef))
            {
                Logger.LogError("PlanCapabilityOffer: capability reference missing/invalid for capability {Capability} (module={Module})", request.Capability, moduleId);
                return NodeStatus.Failure;
            }

            offeredCapability.OfferedCapabilityReference.Value = new ReferenceElementValue(modelRef);
        }

        var action = new ActionModel(
            idShort: $"Action_{request.Capability}",
            actionTitle: request.Capability,
            status: ActionStatusEnum.OPEN,
            inputParameters: null,
            finalResultData: null,
            preconditions: null,
            skillReference: null,
            machineName: station);

        var requestContainer = request.CapabilityContainer;
        var resourceContainer = ResolveLocalCapabilityContainer(request.Capability);

        if (requestContainer != null)
        {
            CopyPropertyContainersToInputParameters(action, requestContainer);
            CopyPostConstraintsToPostconditions(action, requestContainer);
        }
        else if (resourceContainer != null)
        {
            Logger.LogDebug("PlanCapabilityOffer: Using local capability container for property hints of {Capability}.", request.Capability);
            CopyPropertyContainersToInputParameters(action, resourceContainer);
        }
        else
        {
            Logger.LogWarning("PlanCapabilityOffer: No capability container available for property hints of {Capability}.", request.Capability);
        }

        if (resourceContainer != null)
        {
            CopyPreConstraintsToPreconditions(action, resourceContainer);
            CopyPostConstraintsToPostconditions(action, resourceContainer);
            if (!TryLinkSkillReference(action, resourceContainer))
            {
                Logger.LogWarning("PlanCapabilityOffer: Local capability container {Container} lacks a RealizedBy relation.", resourceContainer.IdShort ?? resourceContainer.GetCapabilityName());
            }
            else
            {
                Logger.LogDebug("PlanCapabilityOffer: Skill reference resolved from local capability description for capability {Capability}.", request.Capability);
            }
        }
        else
        {
            Logger.LogWarning("PlanCapabilityOffer: Missing local capability container for capability {Capability}; constraints and skill reference not applied.", request.Capability);
        }
        offeredCapability.AddAction(action);

        plan.OfferedCapability = offeredCapability;
        var transportSequence = Context.Get<List<TransportSequenceItem>>("Planning.TransportSequence");
        if (transportSequence != null && transportSequence.Count > 0)
        {
            foreach (var entry in transportSequence)
            {
                if (entry == null)
                {
                    continue;
                }

                var transportOffer = entry.Capability;
                if (transportOffer == null)
                {
                    continue;
                }

                EnsureOfferHasActionInputParameters(transportOffer, fallbackActionTitle: "Transport");

                transportOffer.SetSequencePlacement(ConvertPlacement(entry.Placement));
                plan.SupplementalCapabilities.Add(transportOffer);
            }
        }
        else
        {
            var supplementalOffers = Context.Get<List<OfferedCapability>>("Planning.TransportOffers");
            if (supplementalOffers != null && supplementalOffers.Count > 0)
            {
                foreach (var transportOffer in supplementalOffers)
                {
                    if (transportOffer == null)
                    {
                        continue;
                    }

                    transportOffer.SetSequencePlacement("pre");
                    plan.SupplementalCapabilities.Add(transportOffer);
                }
            }
        }
        Context.Set("Planning.TransportSequence", null);
        Context.Set("Planning.TransportOffers", null);

        Context.Set("Planning.CapabilityOffer", plan);
        Logger.LogInformation("PlanCapabilityOffer: planned start={Start} cost={Cost}", plannedStart, plan.Cost);
        return NodeStatus.Success;
    }

    private void EnsureOfferHasActionInputParameters(OfferedCapability offer, string fallbackActionTitle)
    {
        if (offer == null)
        {
            Logger.LogDebug("EnsureOfferHasActionInputParameters: offer is null");
            try { Console.WriteLine("[DEBUG] EnsureOfferHasActionInputParameters: offer is null"); } catch {}
            return;
        }

        Logger.LogDebug("EnsureOfferHasActionInputParameters: inspecting offer instanceId={Instance}", offer.InstanceIdentifier.GetText());
        try { Console.WriteLine($"[DEBUG] EnsureOfferHasActionInputParameters: inspecting offer instanceId={offer.InstanceIdentifier.GetText()}"); } catch {}

        // Accept both typed ActionModel and generic SubmodelElementCollection actions.
        // IMPORTANT: Keep transport actions 1:1 when possible.
        var hadAnyAction = false;
        foreach (var actionElement in offer.Actions)
        {
            hadAnyAction = true;
            if (actionElement is ActionModel actionModel)
            {
                var pcount = actionModel.InputParameters?.Parameters?.Count ?? 0;
                Logger.LogDebug("EnsureOfferHasActionInputParameters: found typed action {Title} inputParams={Count}", actionModel.ActionTitle, pcount);
                if (actionModel.InputParameters != null)
                {
                    return;
                }
            }
            else if (actionElement is SubmodelElementCollection actionCollection)
            {
                var hasInputParameters = (actionCollection.Values ?? Array.Empty<ISubmodelElement>())
                    .OfType<SubmodelElementCollection>()
                    .Any(smc => string.Equals(smc.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase));

                Logger.LogDebug("EnsureOfferHasActionInputParameters: found action collection {IdShort} hasInputParameters={HasIp}", actionCollection.IdShort, hasInputParameters);

                if (hasInputParameters)
                {
                    return;
                }

                try
                {
                    actionCollection.Add(new InputParameters());
                    Logger.LogDebug("EnsureOfferHasActionInputParameters: injected InputParameters collection into action collection {IdShort}", actionCollection.IdShort);
                    try { Console.WriteLine($"[DEBUG] EnsureOfferHasActionInputParameters: injected InputParameters into {actionCollection.IdShort}"); } catch {}
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "EnsureOfferHasActionInputParameters: failed to mutate action collection {IdShort}", actionCollection.IdShort);
                    try { Console.WriteLine($"[DEBUG] EnsureOfferHasActionInputParameters: failed to inject InputParameters into {actionCollection.IdShort}: {ex.Message}"); } catch {}
                    // If we cannot mutate the collection, fall back to adding a new action below.
                }
            }
        }

        if (hadAnyAction)
        {
            Logger.LogDebug("EnsureOfferHasActionInputParameters: had actions but none with InputParameters and mutation failed; will add fallback action");
        }

        var machineName = offer.Station.GetText() ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(fallbackActionTitle) ? "Action" : fallbackActionTitle.Trim();

        // Minimal action: empty InputParameters collection is sufficient for dispatcher validation.
        var fallbackAction = new ActionModel(
            idShort: "Action_Transport",
            actionTitle: title,
            status: ActionStatusEnum.PLANNED,
            inputParameters: null,
            finalResultData: null,
            preconditions: null,
            skillReference: null,
            machineName: machineName);

        offer.AddAction(fallbackAction);
        Logger.LogDebug("EnsureOfferHasActionInputParameters: added fallback action {Title}", title);
        try { Console.WriteLine($"[DEBUG] EnsureOfferHasActionInputParameters: added fallback action {title}"); } catch {}
    }

    private sealed record Neo4jReferenceKey(string? Type, string? Value);

    private static bool TryParseNeo4jReferenceJson(string json, out Reference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        List<Neo4jReferenceKey>? keysDto;
        try
        {
            keysDto = JsonSerializer.Deserialize<List<Neo4jReferenceKey>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return false;
        }

        if (keysDto == null || keysDto.Count == 0)
        {
            return false;
        }

        var keys = new List<IKey>();
        foreach (var dto in keysDto)
        {
            var typeText = dto?.Type;
            var valueText = dto?.Value;
            if (string.IsNullOrWhiteSpace(typeText) || string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            if (!TryMapKeyType(typeText!, out var keyType))
            {
                continue;
            }

            keys.Add(new Key(keyType, valueText!.Trim()));
        }

        if (keys.Count == 0)
        {
            return false;
        }

        reference = new Reference(keys)
        {
            Type = ReferenceType.ModelReference
        };
        return true;
    }

    private static bool TryMapKeyType(string typeText, out KeyType keyType)
    {
        keyType = default;

        if (string.IsNullOrWhiteSpace(typeText))
        {
            return false;
        }

        if (Enum.TryParse<KeyType>(typeText.Trim(), ignoreCase: true, out var parsed))
        {
            keyType = parsed;
            return true;
        }

        // tolerate common variations
        if (string.Equals(typeText, "SubmodelElementCollection", StringComparison.OrdinalIgnoreCase))
        {
            keyType = KeyType.SubmodelElementCollection;
            return true;
        }

        return false;
    }

    private static void CopyPropertyContainersToInputParameters(ActionModel action, CapabilityContainer container)
    {
        var inputParameters = action.InputParameters;
        if (inputParameters == null)
        {
            return;
        }

        foreach (var (key, value) in EnumeratePropertyContainerParameters(container))
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            inputParameters.SetParameter(key, value);
        }
    }

    private static IEnumerable<(string Key, object? Value)> EnumeratePropertyContainerParameters(CapabilityContainer container)
    {
        foreach (var section in container.PropertyContainers)
        {
            var values = section?.Source?.Values;
            if (values == null)
            {
                continue;
            }

            foreach (var property in values.OfType<Property>())
            {
                var key = ResolveParameterKey(property.IdShort, section?.Source?.IdShort);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                yield return (key, ExtractElementValue(property.Value));
            }

            foreach (var range in values.OfType<RangeElement>())
            {
                var baseKey = ResolveParameterKey(range.IdShort, section?.Source?.IdShort);
                if (string.IsNullOrWhiteSpace(baseKey))
                {
                    continue;
                }

                var min = range.Value?.Min?.Value;
                var max = range.Value?.Max?.Value;
                if (min != null)
                {
                    yield return ($"{baseKey}.Min", ExtractElementValue(min));
                }

                if (max != null)
                {
                    yield return ($"{baseKey}.Max", ExtractElementValue(max));
                }
            }

            foreach (var list in values.OfType<SubmodelElementList>())
            {
                var index = 0;
                foreach (var element in list.OfType<Property>())
                {
                    var key = ResolveParameterKey(element.IdShort, section?.Source?.IdShort);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var finalKey = index > 0 ? $"{key}.{index}" : key;
                    yield return (finalKey, ExtractElementValue(element.Value));
                    index++;
                }
            }
        }
    }

    private static void CopyPreConstraintsToPreconditions(ActionModel action, CapabilityContainer container)
    {
        var preconditions = action.Preconditions;
        if (preconditions == null)
        {
            return;
        }

        var nextIndex = GetNextConditionValueIndex(preconditions);

        foreach (var constraint in container.Constraints)
        {
            var conditionalType = GetStringValue(constraint.ConditionalType);
            if (!string.Equals(conditionalType, "pre", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var conditionValue = CreateConditionValueEntry(constraint, ++nextIndex);
            if (conditionValue != null)
            {
                preconditions.Add(conditionValue);
            }
        }
    }

    private static void CopyPostConstraintsToPostconditions(ActionModel action, CapabilityContainer container)
    {
        var postconditions = action.Postconditions;
        if (postconditions == null)
        {
            return;
        }

        var nextIndex = GetNextConditionValueIndex(postconditions);

        foreach (var constraint in container.Constraints)
        {
            var conditionalType = GetStringValue(constraint.ConditionalType);
            if (!string.Equals(conditionalType, "post", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var conditionValue = CreateConditionValueEntry(constraint, ++nextIndex);
            if (conditionValue != null)
            {
                postconditions.Add(conditionValue);
            }
        }
    }

    private static int GetNextConditionValueIndex(SubmodelElementCollection preconditions)
    {
        var values = preconditions?.Values ?? Array.Empty<ISubmodelElement>();
        var max = 0;

        foreach (var collection in values.OfType<SubmodelElementCollection>())
        {
            var idShort = collection.IdShort;
            if (string.IsNullOrWhiteSpace(idShort) || !idShort.StartsWith("ConditionValue_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = idShort.Substring("ConditionValue_".Length);
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > max)
            {
                max = parsed;
            }
        }

        return max;
    }

    private static SubmodelElementCollection? CreateConditionValueEntry(PropertyConstraintContainerSection constraint, int index)
    {
        var custom = constraint.CustomConstraint;
        if (custom == null)
        {
            return null;
        }

        var collection = new SubmodelElementCollection($"ConditionValue_{index:000}");
        var added = false;

        foreach (var property in custom.Properties)
        {
            if (ShouldSkipConditionValueEntry(property))
            {
                continue;
            }

            collection.Add(CloneProperty(property));
            added = true;
        }

        return added ? collection : null;
    }

    private static bool ShouldSkipConditionValueEntry(Property? property)
    {
        var idShort = property?.IdShort;
        if (string.IsNullOrWhiteSpace(idShort))
        {
            return false;
        }

        return string.Equals(idShort, "Reference", StringComparison.OrdinalIgnoreCase)
               || string.Equals(idShort, "embedding", StringComparison.OrdinalIgnoreCase);
    }

    private static Property CloneProperty(Property source)
    {
        var semanticId = source.SemanticId as Reference;
        var created = AasSubmodelElementFactory.CreateProperty(source.IdShort, ExtractElementValue(source.Value), semanticId);
        if (created is Property property)
        {
            return property;
        }

        throw new InvalidOperationException("Failed to clone constraint property");
    }

    private static bool TryLinkSkillReference(ActionModel action, CapabilityContainer container)
    {
        var relation = container.SkillRelation ?? container.RealizedBy.FirstOrDefault();
        var reference = relation?.Value?.Second ?? relation?.Value?.First;
        if (reference == null)
        {
            return false;
        }

        var cloned = CloneReference(reference);
        if (cloned == null)
        {
            return false;
        }

        action.SkillReference.Value = new ReferenceElementValue(cloned);
        return true;
    }

    private static Reference? CloneReference(IReference? reference)
    {
        if (reference == null)
        {
            return null;
        }

        var keyList = reference.Keys?
            .Select(k => (IKey)new Key(k.Type, k.Value))
            .ToList();
        if (keyList == null || keyList.Count == 0)
        {
            return null;
        }

        return new Reference(keyList)
        {
            Type = reference.Type
        };
    }

    private static string? ResolveParameterKey(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private CapabilityContainer? ResolveLocalCapabilityContainer(string capabilityName)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return null;
        }

        var submodel = Context.Get<CapabilityDescriptionSubmodel>("CapabilityDescriptionSubmodel");
        if (submodel?.CapabilitySet == null)
        {
            return null;
        }

        return submodel.CapabilitySet
            .OfType<SubmodelElementCollection>()
            .Select(collection => new CapabilityContainer(collection))
            .FirstOrDefault(container => string.Equals(container.GetCapabilityName(), capabilityName, StringComparison.OrdinalIgnoreCase));
    }

    private static object? ExtractElementValue(object? value)
    {
        return AasValueUnwrap.Unwrap(value);
    }

    private static string ConvertPlacement(TransportPlacement placement)
    {
        return placement == TransportPlacement.AfterCapability ? "post" : "pre";
    }

    private static string? GetStringValue(Property? property)
    {
        var raw = ExtractElementValue(property?.Value);
        return raw?.ToString();
    }
}

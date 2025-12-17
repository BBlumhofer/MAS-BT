using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning.ProcessChain;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using UAClient.Client;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// RequestTransport - sends an I4.0 transport request to the dispatcher (manufacturing-sequence mode only).
/// </summary>
public class RequestTransportNode : BTNode
{
    public string FromStation { get; set; } = string.Empty;
    public string ToStation { get; set; } = string.Empty;
    public int ResponseTimeoutSeconds { get; set; } = 15;

    public RequestTransportNode() : base("RequestTransport") {}

    public override async Task<NodeStatus> Execute()
    {
        var requestContext = Context.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        if (requestContext == null)
        {
            Logger.LogError("RequestTransport: capability request context missing");
            return NodeStatus.Failure;
        }

        var requirements = Context.Get<List<TransportRequirement>>("Planning.TransportRequirements")
                            ?? new List<TransportRequirement>();
        if (requirements.Count == 0)
        {
            var requiresLegacy = Context.Get<bool?>("RequiresTransport") ?? false;
            if (!requiresLegacy || !requestContext.IsManufacturingRequest)
            {
                Logger.LogDebug("RequestTransport: no transport requirements detected for this capability");
                Context.Set("TransportAccepted", true);
                Context.Set("Planning.TransportOffers", null);
                Context.Set("Planning.TransportSequence", null);
                return NodeStatus.Success;
            }
        }

        if (!requestContext.IsManufacturingRequest)
        {
            Logger.LogDebug("RequestTransport: current CfP subtype {Subtype} does not request transports", requestContext.MessageSubtype);
            Context.Set("TransportAccepted", true);
            Context.Set("Planning.TransportSequence", null);
            return NodeStatus.Success;
        }

        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("RequestTransport: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace");
        if (string.IsNullOrWhiteSpace(ns))
        {
            Logger.LogError("RequestTransport: missing config.Namespace");
            return NodeStatus.Failure;
        }

        var topic = $"/{ns}/TransportPlan";
        // Ensure we are subscribed to the transport topic to receive responses in this in-process test transport.
        try
        {
            await client.SubscribeAsync(topic).ConfigureAwait(false);
            try { Console.WriteLine($"[DEBUG] RequestTransport: subscribed to topic {topic}"); } catch {}
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "RequestTransport: failed to subscribe to {Topic}", topic);
        }
        var aggregatedOffers = new List<OfferedCapability>();
        var sequenceItems = new List<TransportSequenceItem>();
        var allAccepted = true;

        var orderedRequirements = requirements.Count > 0
            ? requirements
            : new List<TransportRequirement>
            {
                new TransportRequirement
                {
                    Target = Context.Get<string>("TransportTarget") ?? string.Empty,
                    Placement = TransportPlacement.BeforeCapability
                }
            };

        for (var index = 0; index < orderedRequirements.Count; index++)
        {
            var requirement = orderedRequirements[index];
            var target = string.IsNullOrWhiteSpace(requirement.Target)
                ? (Context.Get<string>("TransportTarget") ?? requestContext.Capability)
                : requirement.Target;

            if (string.IsNullOrWhiteSpace(target))
            {
                Logger.LogWarning("RequestTransport: requirement #{Index} missing target station, skipping", index);
                continue;
            }

            var element = BuildTransportRequestElement(requestContext, target, requirement, index);
            var convId = Guid.NewGuid().ToString();

            var request = new TransportPlanRequestMessage(
                Context.AgentId,
                Context.AgentRole,
                $"{ns}/DispatchingAgent",
                "DispatchingAgent",
                convId,
                element);

            try { Console.WriteLine($"[DEBUG] BuildTransportRequest: InstanceId={element.InstanceIdentifier?.Value?.Value} IdentifierValue={element.IdentifierValue?.Value?.Value} GoalStation={element.TransportGoalStation?.Value?.Value}"); } catch {}
            // Attach a generic OnMessage logger to help debug in-process delivery (will not interfere with OnConversation)
            try
            {
                client.OnMessage(msg =>
                {
                    try { Console.WriteLine($"[DEBUG] OnMessage (planning client): frameType={msg?.Frame?.Type} convId={msg?.Frame?.ConversationId} elements={msg?.InteractionElements?.Count ?? 0}"); } catch {}
                });
            }
            catch { }

            // Start listening for the response before publishing to avoid races in in-process tests
            var responseTask = AwaitTransportResponseAsync(client, convId);
            var publishedAt = DateTimeOffset.UtcNow;
            await request.PublishAsync(client, topic).ConfigureAwait(false);
            Logger.LogInformation(
                "RequestTransport: published transport request #{Index} (requirement={Requirement}, placement={Placement}, target={Target}, source={Source}, convId={Conv}, publishedAt={Timestamp:o})",
                index + 1,
                requestContext.RequirementId,
                requirement.Placement,
                target,
                requirement.SourceId ?? requestContext.RequirementId,
                convId,
                publishedAt);
            try { Console.WriteLine($"[DEBUG] RequestTransport: published transport request #{index+1} topic={topic} convId={convId}"); } catch {}

            var response = await responseTask.ConfigureAwait(false);
            if (response == null)
            {
                Logger.LogWarning("RequestTransport: no transport response for convId={Conv} (timeout {Timeout}s)", convId, ResponseTimeoutSeconds);
                allAccepted = false;
                continue;
            }

                try { Console.WriteLine($"[DEBUG] RequestTransport: received response for convId={convId} elements={response.InteractionElements?.Count ?? 0}"); } catch {}

            var transportOffers = ExtractTransportOffers(response);
            if (transportOffers.Count == 0)
            {
                Logger.LogInformation("RequestTransport: received response convId={Conv} without offers", convId);
                continue;
            }

            foreach (var offer in transportOffers)
            {
                offer.SetSequencePlacement(ConvertPlacement(requirement.Placement));
                aggregatedOffers.Add(offer);
                sequenceItems.Add(new TransportSequenceItem(requirement.Placement, offer));
            }

            Logger.LogInformation(
                "RequestTransport: received {Count} transport offer(s) for convId={Conv} (placement={Placement})",
                transportOffers.Count,
                convId,
                requirement.Placement);
        }

        Context.Set("TransportAccepted", allAccepted && sequenceItems.Count > 0);
        Context.Set("Planning.TransportOffers", aggregatedOffers.Count > 0 ? aggregatedOffers : null);
        Context.Set("Planning.TransportSequence", sequenceItems.Count > 0 ? sequenceItems : null);
        Context.Set("Planning.TransportRequirements", null);

        return NodeStatus.Success;
    }

    private TransportRequestMessage BuildTransportRequestElement(CapabilityRequestContext request, string target, TransportRequirement requirement, int sequenceNumber)
    {
        var suffix = sequenceNumber >= 0 ? $":{sequenceNumber + 1}" : string.Empty;
        var instanceIdentifier = $"{request.RequirementId}{suffix}";

        var element = new TransportRequestMessage();
        element.InstanceIdentifier.Value = new PropertyValue<string>(instanceIdentifier);
        element.OfferedCapabilityIdentifier.Value = new PropertyValue<string>($"{Context.AgentId}:{instanceIdentifier}");
        element.TransportGoalStation.Value = new PropertyValue<string>(target ?? string.Empty);
        element.SetIdentifierType(TransportRequestMessage.IdentifierTypeEnum.ProductId);
        // Determine IdentifierValue: prefer request.ProductId, then constraint placeholder resolution, then fallback to requirement id
        var identifierValue = string.IsNullOrWhiteSpace(request.ProductId) ? request.RequirementId : request.ProductId;

        if (!string.IsNullOrWhiteSpace(requirement?.ProductIdPlaceholder))
        {
            var placeholder = requirement.ProductIdPlaceholder!.Trim();
            if (!string.Equals(placeholder, "*", StringComparison.Ordinal))
            {
                // literal provided in constraint
                identifierValue = placeholder;
                requirement.ResolvedProductId = identifierValue;
            }
            else
            {
                // wildcard: try resolve from request, ModuleInventory, or RemoteServer
                string? resolved = null;

                // 1) request.ProductId already used above; if it exists, take it
                if (!string.IsNullOrWhiteSpace(request.ProductId)) resolved = request.ProductId;

                // 2) try ModuleInventory snapshot in context
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    var storageUnits = Context.Get<List<StorageUnit>>("ModuleInventory");
                    if (storageUnits != null)
                    {
                        // prefer storage with matching name/target
                        var match = storageUnits.FirstOrDefault(s => string.Equals(s.Name, requirement.Target, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                        {
                            // fallback: first non-empty product id anywhere
                            foreach (var su in storageUnits)
                            {
                                var slot = su.Slots?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Content?.ProductID));
                                if (slot != null && !string.IsNullOrWhiteSpace(slot.Content?.ProductID))
                                {
                                    resolved = slot.Content.ProductID;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            var slot = match.Slots?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Content?.ProductID));
                            if (slot != null) resolved = slot.Content.ProductID;
                        }
                    }
                }

                // 3) try RemoteServer model
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    try
                    {
                        var server = Context.Get<RemoteServer>("RemoteServer");
                        if (server?.Modules != null)
                        {
                            foreach (var modKv in server.Modules)
                            {
                                var storages = modKv.Value?.Storages;
                                if (storages == null) continue;
                                foreach (var st in storages)
                                {
                                    var sKey = st.Key;
                                    var sVal = st.Value;
                                    if (!string.Equals(sKey, requirement.Target, StringComparison.OrdinalIgnoreCase) && !string.Equals(sVal?.Name, requirement.Target, StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    if (sVal?.Slots != null)
                                    {
                                        foreach (var slot in sVal.Slots.Values)
                                        {
                                            if (!string.IsNullOrWhiteSpace(slot.ProductId))
                                            {
                                                resolved = slot.ProductId;
                                                break;
                                            }
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(resolved)) break;
                                }

                                if (!string.IsNullOrWhiteSpace(resolved)) break;
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }

                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    identifierValue = resolved;
                    requirement.ResolvedProductId = resolved;
                }
            }
        }
        element.IdentifierValue.Value = new PropertyValue<string>(identifierValue);
        element.SetAmount(1);
        return element;
    }

    private async Task<I40Message?> AwaitTransportResponseAsync(MessagingClient client, string conversationId)
    {
        var timeoutSeconds = ResponseTimeoutSeconds <= 0 ? 15 : ResponseTimeoutSeconds;
        var tcs = new TaskCompletionSource<I40Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnConversation(conversationId, msg =>
        {
            try
            {
                // print raw frame type for debugging
                try { Console.WriteLine($"[DEBUG] AwaitTransportResponse: received frameType={msg?.Frame?.Type} interactionElements={msg?.InteractionElements?.Count ?? 0} for conv={conversationId}"); } catch {}

                if (!IsTransportResponseFrame(msg?.Frame?.Type))
                {
                    Logger.LogDebug("RequestTransport: ignoring message Type={Type} for conversation {Conv}", msg?.Frame?.Type, conversationId);
                    return;
                }

                if (msg != null)
                {
                    // dump info about interaction elements
                    try
                    {
                        if (msg.InteractionElements != null)
                        {
                            foreach (var el in msg.InteractionElements)
                            {
                                if (el is OfferedCapability oc)
                                {
                                    try
                                    {
                                        var idVal = string.Empty;
                                        try { if (oc.InstanceIdentifier?.Value != null) idVal = AasValueUnwrap.UnwrapToString(oc.InstanceIdentifier.Value) ?? string.Empty; } catch {}
                                        var stationVal = string.Empty;
                                        try { if (oc.Station?.Value != null) stationVal = AasValueUnwrap.UnwrapToString(oc.Station.Value) ?? string.Empty; } catch {}
                                        var actionsCount = 0;
                                        try { actionsCount = oc.Actions != null ? oc.Actions.OfType<ISubmodelElement>().Count() : 0; } catch {}
                                        Console.WriteLine($"[DEBUG] AwaitTransportResponse: element OfferedCapability InstanceId={idVal} Station={stationVal} Actions={actionsCount}");
                                    }
                                    catch { }
                                }
                                else if (el is SubmodelElementCollection col)
                                {
                                    try
                                    {
                                        var childCount = col.Values != null ? col.Values.Count() : 0;
                                        Console.WriteLine($"[DEBUG] AwaitTransportResponse: element Collection IdShort={col.IdShort} children={childCount}");
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }

                    tcs.TrySetResult(msg);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "RequestTransport: failed to enqueue transport response");
            }
        });

        try
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            if (completed == tcs.Task)
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            try
            {
                client.CompleteConversation(conversationId);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static bool IsTransportResponseFrame(string? frameType)
    {
        if (string.IsNullOrWhiteSpace(frameType))
        {
            return false;
        }

        var slashIndex = frameType.IndexOf('/');
        var primary = slashIndex >= 0 ? frameType[..slashIndex] : frameType;
        var subtypeToken = slashIndex >= 0 ? frameType[(slashIndex + 1)..] : string.Empty;

        if (!string.Equals(primary, I40MessageTypes.CONSENT, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(primary, I40MessageTypes.INFORM_CONFIRM, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(subtypeToken))
        {
            return false;
        }

        return I40MessageTypeSubtypesExtensions.TryParse(subtypeToken, out var parsed)
               && parsed == I40MessageTypeSubtypes.TransportRequest;
    }

    private List<OfferedCapability> ExtractTransportOffers(I40Message? message)
    {
        var offers = new List<OfferedCapability>();
        if (message?.InteractionElements == null)
        {
            return offers;
        }

        foreach (var element in message.InteractionElements)
        {
            CollectOfferedCapabilities(element, offers);
        }

        foreach (var offer in offers)
        {
            if (offer.InstanceIdentifier.IsNullOrWhiteSpace())
            {
                offer.InstanceIdentifier.Value = new PropertyValue<string>($"transport_{Guid.NewGuid():N}");
            }
        }

        return offers;
    }

    private void CollectOfferedCapabilities(ISubmodelElement? element, IList<OfferedCapability> sink)
    {
        if (element == null)
        {
            return;
        }

        switch (element)
        {
            case OfferedCapability offer:
                sink.Add(offer);
                break;
            case SubmodelElementCollection collection when LooksLikeOfferedCapability(collection):
                sink.Add(CreateOfferedCapabilityFromCollection(collection));
                break;
            case SubmodelElementCollection collection when collection.Values != null:
                foreach (var child in collection.Values)
                {
                    CollectOfferedCapabilities(child, sink);
                }
                break;
            case SubmodelElementList list:
                foreach (var child in list)
                {
                    CollectOfferedCapabilities(child, sink);
                }
                break;
        }
    }

    private static bool LooksLikeOfferedCapability(SubmodelElementCollection collection)
    {
        if (collection == null)
        {
            return false;
        }

        var values = collection.Values ?? Array.Empty<ISubmodelElement>();
        var hasInstanceId = values.OfType<Property>()
            .Any(p => string.Equals(p.IdShort, OfferedCapability.InstanceIdentifierIdShort, StringComparison.OrdinalIgnoreCase));
        var hasReference = values.OfType<ReferenceElement>()
            .Any(r => string.Equals(r.IdShort, OfferedCapability.OfferedCapabilityReferenceIdShort, StringComparison.OrdinalIgnoreCase));
        return hasInstanceId || hasReference;
    }

    private OfferedCapability CreateOfferedCapabilityFromCollection(SubmodelElementCollection source)
    {
        var offer = new OfferedCapability(string.Empty);
        if (source == null)
        {
            return offer;
        }

        offer.SemanticId = source.SemanticId;
        offer.Description = source.Description;
        offer.Qualifiers = source.Qualifiers;

        var children = source.Values ?? Array.Empty<ISubmodelElement>();
        foreach (var child in children)
        {
            switch (child)
            {
                case ReferenceElement reference when string.Equals(reference.IdShort, OfferedCapability.OfferedCapabilityReferenceIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.OfferedCapabilityReference.Value = reference.Value;
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.InstanceIdentifierIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.InstanceIdentifier.SetText(AasValueUnwrap.UnwrapToString(prop.Value) ?? string.Empty);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.StationIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.Station.SetText(AasValueUnwrap.UnwrapToString(prop.Value) ?? string.Empty);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.MatchingScoreIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.MatchingScore.Value = new PropertyValue<double>(ParseDouble(AasValueUnwrap.Unwrap(prop.Value)));
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.CostIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.SetCost(ParseDouble(AasValueUnwrap.Unwrap(prop.Value)));
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.SequencePlacementIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.SequencePlacement.SetText(AasValueUnwrap.UnwrapToString(prop.Value) ?? string.Empty);
                    break;
                case SubmodelElementCollection collection when string.Equals(collection.IdShort, OfferedCapability.EarliestSchedulingInformationIdShort, StringComparison.OrdinalIgnoreCase):
                    CopyScheduling(collection, offer);
                    break;
                case SubmodelElementList list when string.Equals(list.IdShort, OfferedCapability.ActionsIdShort, StringComparison.OrdinalIgnoreCase):
                    // iterate by index to allow grouping consecutive primitive elements into a synthetic action collection
                    for (var ai = 0; ai < list.Count; ai++)
                    {
                        var actionElement = list[ai];
                        if (actionElement is AasSharpClient.Models.Action action)
                        {
                            Logger.LogDebug("CreateOfferedCapabilityFromCollection: found typed Action {Title} with InputParameters={Count}", action.ActionTitle, action.InputParameters?.Parameters?.Count ?? 0);
                            try { Console.WriteLine($"[DEBUG] CreateOfferedCapabilityFromCollection: found typed Action {action.ActionTitle} inputParams={action.InputParameters?.Parameters?.Count ?? 0}"); } catch {}
                            offer.AddAction(action);
                            continue;
                        }

                        if (actionElement is SubmodelElementCollection actionCollection)
                        {
                            var mat = MaterializeAction(actionCollection);
                            Logger.LogDebug("CreateOfferedCapabilityFromCollection: materialized action from collection {IdShort} with InputParameters={Count}", actionCollection.IdShort, mat.InputParameters?.Parameters?.Count ?? 0);
                            try { Console.WriteLine($"[DEBUG] CreateOfferedCapabilityFromCollection: materialized action from {actionCollection.IdShort} inputParams={mat.InputParameters?.Parameters?.Count ?? 0}"); } catch {}
                            offer.AddAction(mat);
                            continue;
                        }

                        // If we encounter primitive elements (Property, ReferenceElement, etc.) fold consecutive primitives into one Action collection
                        if (actionElement is Property || actionElement is ReferenceElement || actionElement is SubmodelElementList || actionElement is ISubmodelElement)
                        {
                            var synthetic = new SubmodelElementCollection("Action");
                            // gather this and following consecutive primitive elements that are not SubmodelElementCollection
                            var j = ai;
                            for (; j < list.Count; j++)
                            {
                                var el = list[j];
                                if (el is SubmodelElementCollection) break;
                                synthetic.Add(el);
                            }

                            // advance outer index
                            ai = j - 1;

                            // materialize synthetic collection
                            var mat = MaterializeAction(synthetic);
                            Logger.LogDebug("CreateOfferedCapabilityFromCollection: materialized synthetic action from primitives with InputParameters={Count}", mat.InputParameters?.Parameters?.Count ?? 0);
                            try { Console.WriteLine($"[DEBUG] CreateOfferedCapabilityFromCollection: materialized synthetic action inputParams={mat.InputParameters?.Parameters?.Count ?? 0}"); } catch {}
                            offer.AddAction(mat);
                            continue;
                        }
                    }
                    break;
            }
        }

        return offer;
    }

    private AasSharpClient.Models.Action MaterializeAction(SubmodelElementCollection source)
    {
        // Default values; we keep this strict/simple and only map what we need downstream.
        var title = FindPropertyValue(source, "ActionTitle") ?? "Action";
        var machineName = FindPropertyValue(source, "MachineName") ?? string.Empty;

        var statusRaw = FindPropertyValue(source, "Status") ?? string.Empty;
        var status = ActionStatusEnum.PLANNED;
        if (!string.IsNullOrWhiteSpace(statusRaw) && Enum.TryParse<ActionStatusEnum>(statusRaw, ignoreCase: true, out var parsed))
        {
            status = parsed;
        }

        // Copy InputParameters (typed) if present.
        InputParameters? inputParameters = null;
        try
        {
            var ip = source.Values?.OfType<SubmodelElementCollection>()
                .FirstOrDefault(c => string.Equals(c.IdShort, "InputParameters", StringComparison.OrdinalIgnoreCase));
            if (ip != null)
            {
                inputParameters = new InputParameters();
                foreach (var prop in ip.OfType<Property>())
                {
                    var key = prop.IdShort ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var value = AasValueUnwrap.UnwrapToString(prop.Value) ?? string.Empty;
                    inputParameters.SetParameter(key, value);
                }
                Logger.LogDebug("MaterializeAction: created InputParameters with {Count} entries from collection {IdShort}", inputParameters.Parameters.Count, source.IdShort);
                try { Console.WriteLine($"[DEBUG] MaterializeAction: created InputParameters with {inputParameters.Parameters.Count} entries from collection {source.IdShort}"); } catch {}
            }
            else
            {
                Logger.LogDebug("MaterializeAction: no InputParameters collection found in {IdShort}", source.IdShort);
                try { Console.WriteLine($"[DEBUG] MaterializeAction: no InputParameters collection found in {source.IdShort}"); } catch {}
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "MaterializeAction: failed to extract InputParameters from {IdShort}", source?.IdShort);
        }

        return new AasSharpClient.Models.Action(
            idShort: "",
            actionTitle: title,
            status: status,
            inputParameters: inputParameters,
            finalResultData: null,
            preconditions: null,
            skillReference: null,
            machineName: machineName);
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
                return AasValueUnwrap.UnwrapToString(prop.Value);
            }
        }

        return null;
    }

    private static void CopyScheduling(SubmodelElementCollection scheduling, OfferedCapability offer)
    {
        var properties = scheduling.Values?.OfType<Property>() ?? Array.Empty<Property>();
        DateTime? start = null;
        DateTime? end = null;
        TimeSpan? setup = null;
        TimeSpan? cycle = null;

        foreach (var property in properties)
        {
            var value = AasValueUnwrap.UnwrapToString(property.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            switch (property.IdShort)
            {
                case "StartDateTime":
                    if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedStart))
                    {
                        start = parsedStart;
                    }
                    break;
                case "EndDateTime":
                    if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedEnd))
                    {
                        end = parsedEnd;
                    }
                    break;
                case "SetupTime":
                    if (TimeSpan.TryParse(value, out var parsedSetup))
                    {
                        setup = parsedSetup;
                    }
                    break;
                case "CycleTime":
                    if (TimeSpan.TryParse(value, out var parsedCycle))
                    {
                        cycle = parsedCycle;
                    }
                    break;
            }
        }

        if (start.HasValue && end.HasValue && setup.HasValue && cycle.HasValue)
        {
            offer.SetEarliestScheduling(start.Value, end.Value, setup.Value, cycle.Value);
        }
    }

    private static double ParseDouble(object? value)
    {
        if (value == null)
        {
            return 0;
        }

        if (value is double d) return d;
        if (value is float f) return f;
        if (value is decimal m) return (double)m;
        if (value is int i) return i;
        if (value is long l) return l;
        var text = value.ToString();
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static string ConvertPlacement(TransportPlacement placement)
    {
        return placement == TransportPlacement.AfterCapability ? "post" : "pre";
    }
}

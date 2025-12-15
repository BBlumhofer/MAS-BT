using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Messages;
using AasSharpClient.Models;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class CollectCapabilityOfferNode : BTNode
{
    private readonly ConcurrentQueue<I40Message> _incoming = new();
    private readonly HashSet<string> _respondedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reissuedCfPs = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscribed;
    private DateTime _startTime;
    private int _expectedModules;
    private HashSet<string>? _expectedModuleIds;
    private bool _drainedInbox;

    public int TimeoutSeconds { get; set; } = 5;

    public CollectCapabilityOfferNode() : base("CollectCapabilityOffer") { }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var ctx = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (client == null || ctx == null)
        {
            Logger.LogError("CollectCapabilityOffer: missing client or context");
            return NodeStatus.Failure;
        }

        if (!_subscribed)
        {
            client.OnConversation(ctx.ConversationId, msg => _incoming.Enqueue(msg));
            _startTime = DateTime.UtcNow;
            _subscribed = true;
            _drainedInbox = false;

            var expected = Context.Get<List<string>>("ProcessChain.ExpectedOfferResponders")
                           ?? Context.Get<List<string>>("ProcessChain.ExpectedOfferResponders".ToLowerInvariant());
            if (expected != null)
            {
                _expectedModuleIds = expected
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(NormalizeModuleId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            if (_expectedModuleIds != null && _expectedModuleIds.Count > 0)
            {
                _expectedModules = _expectedModuleIds.Count;
            }
            else
            {
                var state = Context.Get<DispatchingState>("DispatchingState");
                _expectedModules = state?.Modules.Count ?? 0;
            }

            Logger.LogInformation("CollectCapabilityOffer: waiting for responses from {Count} modules", _expectedModules);
        }

        // Drain buffered messages that may have arrived before the conversation callback was registered.
        // This is critical in fast in-memory transports / local MQTT where replies can come back immediately.
        DrainInbox(client, ctx.ConversationId);

        // If a target module registers AFTER we dispatched CfPs, it will have missed the CfP publish.
        // Re-issue the cached CfPs once for that module to make the negotiation robust against late startup.
        await TryReissueCfPsForLateModulesAsync(client).ConfigureAwait(false);

        while (_incoming.TryDequeue(out var message))
        {
            ProcessMessage(ctx, message);
        }

        var complete = ctx.Requirements.Count > 0 && ctx.Requirements.All(r => r.CapabilityOffers.Count > 0);
        var allModulesResponded = _expectedModules == 0
            || (_expectedModuleIds != null && _expectedModuleIds.Count > 0
                ? _expectedModuleIds.All(id => _respondedModules.Contains(id))
                : _respondedModules.Count >= _expectedModules);
        var timedOut = (DateTime.UtcNow - _startTime).TotalSeconds >= TimeoutSeconds;

        if (complete || allModulesResponded || timedOut)
        {
            if (timedOut)
            {
                Logger.LogWarning("CollectCapabilityOffer: timeout after {Timeout}s", TimeoutSeconds);

                var state = Context.Get<DispatchingState>("DispatchingState");
                List<string> missingModules;
                if (_expectedModuleIds != null && _expectedModuleIds.Count > 0)
                {
                    missingModules = _expectedModuleIds
                        .Where(id => !_respondedModules.Contains(id))
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    var knownModules = state?.Modules.Select(m => m.ModuleId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(NormalizeModuleId).ToList()
                                     ?? new List<string>();
                    missingModules = knownModules
                        .Where(id => !_respondedModules.Contains(id))
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                var missingReqs = ctx.Requirements
                    .Where(r => r.CapabilityOffers.Count == 0)
                    .Select(r => $"{r.Capability}({r.RequirementId})")
                    .ToList();

                Logger.LogWarning(
                    "CollectCapabilityOffer: respondedModules={Responded}/{Expected}. MissingModules=[{MissingModules}] MissingRequirements=[{MissingReqs}]",
                    _respondedModules.Count,
                    _expectedModules,
                    string.Join(",", missingModules),
                    string.Join(",", missingReqs));
            }

            Context.Set("ProcessChain.Negotiation", ctx);
            return NodeStatus.Success;
        }

        return NodeStatus.Running;
    }

    public override Task OnReset()
    {
        _incoming.Clear();
        _respondedModules.Clear();
        _reissuedCfPs.Clear();
        _subscribed = false;
        _expectedModules = 0;
        _expectedModuleIds = null;
        _drainedInbox = false;
        return Task.CompletedTask;
    }

    private async Task TryReissueCfPsForLateModulesAsync(MessagingClient client)
    {
        if (client == null || !client.IsConnected)
        {
            return;
        }

        if (_expectedModuleIds == null || _expectedModuleIds.Count == 0)
        {
            return;
        }

        var state = Context.Get<DispatchingState>("DispatchingState");
        if (state == null)
        {
            return;
        }

        var cfpsByTarget = Context.Get<Dictionary<string, List<I40Message>>>("ProcessChain.CfPsByTarget");
        if (cfpsByTarget == null || cfpsByTarget.Count == 0)
        {
            return;
        }

        var topic = Context.Get<string>("ProcessChain.CfPTopic");
        if (string.IsNullOrWhiteSpace(topic))
        {
            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            topic = $"/{ns}/DispatchingAgent/Offer";
        }

        DateTime dispatchUtc;
        try
        {
            dispatchUtc = Context.Get<DateTime>("ProcessChain.CfPDispatchUtc");
        }
        catch
        {
            dispatchUtc = _startTime;
        }

        // Avoid aggressive re-issues during the very first tick.
        if ((DateTime.UtcNow - _startTime).TotalMilliseconds < 50)
        {
            return;
        }

        foreach (var expected in _expectedModuleIds)
        {
            if (string.IsNullOrWhiteSpace(expected)) continue;
            if (_respondedModules.Contains(expected)) continue;
            if (_reissuedCfPs.Contains(expected)) continue;

            var module = state.Modules.FirstOrDefault(m => string.Equals(NormalizeModuleId(m.ModuleId), expected, StringComparison.OrdinalIgnoreCase));
            if (module == null)
            {
                continue;
            }

            // If the module has registered/been seen AFTER dispatch, it likely missed the original publish.
            var likelyMissedInitialPublish = module.LastRegistrationUtc > dispatchUtc || module.LastSeenUtc > dispatchUtc;
            if (!likelyMissedInitialPublish)
            {
                continue;
            }

            // Find cached CfPs for this module (key may be non-normalized).
            List<I40Message>? cfps = null;
            if (!cfpsByTarget.TryGetValue(expected, out cfps))
            {
                foreach (var kvp in cfpsByTarget)
                {
                    if (string.Equals(NormalizeModuleId(kvp.Key), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        cfps = kvp.Value;
                        break;
                    }
                }
            }

            if (cfps == null || cfps.Count == 0)
            {
                _reissuedCfPs.Add(expected);
                continue;
            }

            foreach (var cfp in cfps)
            {
                try
                {
                    await client.PublishAsync(cfp, topic).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }

            _reissuedCfPs.Add(expected);
            Logger.LogInformation("CollectCapabilityOffer: re-issued {Count} CfP(s) to late-registered module {Module} on {Topic}", cfps.Count, expected, topic);
        }
    }

    private void ProcessMessage(ProcessChainNegotiationContext ctx, I40Message message)
    {
        var requirement = ResolveRequirement(ctx, message);
        if (requirement == null)
        {
            Logger.LogDebug("CollectCapabilityOffer: received message that does not match any requirement");
            return;
        }

        var messageType = message.Frame?.Type ?? string.Empty;
        var sender = message.Frame?.Sender?.Identification?.Id;
        var senderModuleId = NormalizeModuleId(sender);
        if (string.Equals(messageType, I40MessageTypes.PROPOSAL, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(senderModuleId))
            {
                _respondedModules.Add(senderModuleId);
            }

            // De-dup: callbacks/conversation routing can deliver the same proposal more than once.
            var incomingOfferId = ExtractProperty(message, "OfferId") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(incomingOfferId)
                && requirement.CapabilityOffers.Any(o =>
                    string.Equals(o.InstanceIdentifier?.Value?.Value?.ToString() ?? string.Empty, incomingOfferId, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogDebug("CollectCapabilityOffer: ignored duplicate offer {OfferId} for capability {Capability}", incomingOfferId, requirement.Capability);
                return;
            }

            IList<OfferedCapability> offers;
            try
            {
                offers = BuildCapabilityOffers(requirement, message);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "CollectCapabilityOffer: invalid proposal received (cannot extract OfferedCapability)");
                throw;
            }

            foreach (var offer in offers)
            {
                if (offer == null)
                {
                    continue;
                }
                requirement.AddOffer(offer);
                var offerId = offer.InstanceIdentifier.Value?.Value?.ToString() ?? "<unknown>";
                Logger.LogInformation("CollectCapabilityOffer: recorded offer {OfferId} for capability {Capability}", offerId, requirement.Capability);
            }
        }
        else if (string.Equals(messageType, I40MessageTypes.REFUSE_PROPOSAL, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(messageType, I40MessageTypes.REFUSAL, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(senderModuleId))
            {
                _respondedModules.Add(senderModuleId);
            }
            Logger.LogInformation("CollectCapabilityOffer: module {Sender} refused requirement {Requirement}", sender, requirement.RequirementId);
        }
    }

    private static string NormalizeModuleId(string? senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId)) return string.Empty;

        if (senderId.EndsWith("_Execution", StringComparison.OrdinalIgnoreCase))
        {
            return senderId.Substring(0, senderId.Length - "_Execution".Length);
        }

        if (senderId.EndsWith("_Planning", StringComparison.OrdinalIgnoreCase))
        {
            return senderId.Substring(0, senderId.Length - "_Planning".Length);
        }

        return senderId;
    }

    private void DrainInbox(MessagingClient client, string conversationId)
    {
        if (client == null || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        // Limit per tick to avoid starving the BT if inbox is very large.
        var drained = 0;
        const int MaxDrainPerTick = 500;

        while (drained < MaxDrainPerTick
               && client.TryDequeueMatching(
                   (msg, _topic) => string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal),
                   out var msg,
                   out var topic,
                   out var receivedAt))
        {
            _incoming.Enqueue(msg);
            var now = DateTimeOffset.UtcNow;
            var latencyMs = receivedAt == default ? "n/a" : (now - receivedAt).TotalMilliseconds.ToString("F1");
            Logger.LogInformation(
                "CollectCapabilityOffer: buffered message delivered Type={Type} Topic={Topic} ReceivedAt={ReceivedAt:o} LatencyMs={Latency}",
                msg?.Frame?.Type ?? "<unknown>",
                topic,
                receivedAt,
                latencyMs);
            drained++;
        }

        if (drained > 0 && !_drainedInbox)
        {
            Logger.LogDebug("CollectCapabilityOffer: drained {Count} buffered messages from inbox for conversation {Conv}", drained, conversationId);
            _drainedInbox = true;
        }
    }

    private static CapabilityRequirement? ResolveRequirement(ProcessChainNegotiationContext ctx, I40Message message)
    {
        var requirementId = ExtractProperty(message, "RequirementId");
        if (!string.IsNullOrWhiteSpace(requirementId))
        {
            var match = ctx.Requirements.FirstOrDefault(r => string.Equals(r.RequirementId, requirementId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        var capability = ExtractProperty(message, "Capability");
        if (!string.IsNullOrWhiteSpace(capability))
        {
            return ctx.Requirements.FirstOrDefault(r => string.Equals(r.Capability, capability, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static IList<OfferedCapability> BuildCapabilityOffers(CapabilityRequirement requirement, I40Message message)
    {
        var provided = ExtractOfferedCapabilities(message);
        if (provided == null || provided.Count == 0)
        {
            var details = BuildMissingOfferedCapabilityDetails(requirement, message);
            throw new InvalidOperationException(details);
        }

        foreach (var offer in provided)
        {
            EnsureOfferDefaults(requirement, message, offer);
            EnsureOfferHasInputParameters(requirement, message, offer);
        }

        return provided;
    }

    private static void EnsureOfferDefaults(CapabilityRequirement requirement, I40Message message, OfferedCapability offer)
    {
        var offerId = offer.InstanceIdentifier.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(offerId))
        {
            offerId = ExtractProperty(message, "OfferId");
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message, missingFields: new[] { "OfferedCapability.InstanceIdentifier / OfferId" }));
            }
            offer.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
        }

        var station = offer.Station.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(station))
        {
            station = ExtractProperty(message, "Station") ?? message.Frame?.Sender?.Identification?.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(station))
            {
                throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message, missingFields: new[] { "OfferedCapability.Station / Station" }));
            }
            offer.Station.Value = new PropertyValue<string>(station);
        }

        // Scheduling: only set values when they are available from the incoming message or the provided offer.
        // Do NOT invent timestamps (e.g. DateTime.UtcNow) here.
        var start = offer.EarliestSchedulingInformation.GetStartDateTime()
                    ?? ParseDateTimeUtc(ExtractProperty(message, "EarliestStartUtc"));

        var cycle = offer.EarliestSchedulingInformation.GetCycleTime();
        if (cycle == null)
        {
            var cycleMinutes = ParseDouble(ExtractProperty(message, "CycleTimeMinutes"));
            if (cycleMinutes > 0)
            {
                cycle = TimeSpan.FromMinutes(cycleMinutes);
            }
        }

        var setup = offer.EarliestSchedulingInformation.GetSetupTime();
        if (setup == null)
        {
            var setupMinutes = ParseDouble(ExtractProperty(message, "SetupTimeMinutes"));
            if (setupMinutes >= 0)
            {
                setup = TimeSpan.FromMinutes(setupMinutes);
            }
        }

        var end = offer.EarliestSchedulingInformation.GetEndDateTime();
        if (end == null && start != null && cycle != null)
        {
            end = start.Value.Add(cycle.Value);
        }

        if (start != null && end != null && setup != null && cycle != null)
        {
            offer.SetEarliestScheduling(start.Value, end.Value, setup.Value, cycle.Value);
        }

        var cost = ExtractDouble(offer.Cost.Value?.Value);
        if (cost <= 0)
        {
            var parsedCost = ParseDouble(ExtractProperty(message, "Cost"));
            if (parsedCost > 0)
            {
                offer.SetCost(parsedCost);
            }
        }

        if (!offer.Actions.Any())
        {
            // No auto-generated defaults here. Offers must be fully provided by the sender.
        }
    }

    private static List<OfferedCapability> ExtractOfferedCapabilities(I40Message message)
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

        return offers;
    }

    private static void CollectOfferedCapabilities(ISubmodelElement element, IList<OfferedCapability> sink)
    {
        if (element == null)
        {
            return;
        }

        switch (element)
        {
            case OfferedCapability direct:
                sink.Add(direct);
                break;
            case SubmodelElementCollection collection when string.Equals(collection.IdShort, "OfferedCapability", StringComparison.OrdinalIgnoreCase):
                sink.Add(CreateOfferedCapabilityFromCollection(collection));
                break;
            case SubmodelElementCollection collection when collection.Values != null:
                foreach (var child in collection.Values)
                {
                    CollectOfferedCapabilities(child, sink);
                }
                break;
            case SubmodelElementList list when string.Equals(list.IdShort, OfferedCapability.CapabilitySequenceIdShort, StringComparison.OrdinalIgnoreCase):
                // Capability sequences are handled when materializing the parent offer; do not treat entries as standalone offers.
                break;
            case SubmodelElementList list:
                foreach (var child in list)
                {
                    CollectOfferedCapabilities(child, sink);
                }
                break;
        }
    }

    private static OfferedCapability CreateOfferedCapabilityFromCollection(SubmodelElementCollection source)
    {
        var idShort = string.IsNullOrWhiteSpace(source?.IdShort) ? "OfferedCapability" : source!.IdShort;
        var offer = new OfferedCapability(idShort);
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
                    offer.InstanceIdentifier.Value = new PropertyValue<string>(TryExtractString(prop.Value?.Value) ?? string.Empty);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.StationIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.Station.Value = new PropertyValue<string>(TryExtractString(prop.Value?.Value) ?? string.Empty);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.MatchingScoreIdShort, StringComparison.OrdinalIgnoreCase):
                    var score = ExtractDouble(prop.Value?.Value);
                    offer.MatchingScore.Value = new PropertyValue<double>(score);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.CostIdShort, StringComparison.OrdinalIgnoreCase):
                    var detectedCost = ExtractDouble(prop.Value?.Value);
                    if (detectedCost > 0)
                    {
                        offer.SetCost(detectedCost);
                    }
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.SequencePlacementIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.SequencePlacement.Value = new PropertyValue<string>(TryExtractString(prop.Value?.Value) ?? string.Empty);
                    break;
                case SubmodelElementCollection collection when string.Equals(collection.IdShort, OfferedCapability.EarliestSchedulingInformationIdShort, StringComparison.OrdinalIgnoreCase):
                    CopySchedulingFromCollection(offer, collection);
                    break;
                case SubmodelElementList list when string.Equals(list.IdShort, OfferedCapability.ActionsIdShort, StringComparison.OrdinalIgnoreCase):
                    foreach (var actionElement in list)
                    {
                        if (actionElement is ActionModel actionModel)
                        {
                            offer.AddAction(actionModel);
                        }
                        else if (actionElement is ISubmodelElement element)
                        {
                            offer.Actions.Add(EnsureListChildHasNoIdShort(element));
                        }
                    }
                    break;
                case SubmodelElementList list when string.Equals(list.IdShort, OfferedCapability.CapabilitySequenceIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.CapabilitySequence.Clear();
                    foreach (var seqElement in list)
                    {
                        if (TryMaterializeOfferedCapability(seqElement, out var nestedCap))
                        {
                            offer.CapabilitySequence.Add(nestedCap);
                        }
                        else
                        {
                            offer.CapabilitySequence.Add(EnsureListChildHasNoIdShort(seqElement));
                        }
                    }
                    break;
            }
        }

        return offer;
    }

    private static void CopySchedulingFromCollection(OfferedCapability offer, SubmodelElementCollection collection)
    {
        DateTime? start = null;
        DateTime? end = null;
        TimeSpan? setup = null;
        TimeSpan? cycle = null;

        var elements = collection.Values ?? Array.Empty<ISubmodelElement>();
        foreach (var element in elements.OfType<Property>())
        {
            var value = TryExtractString(element.Value?.Value);
            switch (element.IdShort)
            {
                case "StartDateTime":
                    start = ParseDateTimeUtc(value);
                    break;
                case "EndDateTime":
                    end = ParseDateTimeUtc(value);
                    break;
                case "SetupTime":
                    setup = ParseTimeSpanValue(value);
                    break;
                case "CycleTime":
                    cycle = ParseTimeSpanValue(value);
                    break;
            }
        }

        // Do NOT invent timestamps when not provided.
        if (start != null && end != null && setup != null && cycle != null)
        {
            offer.SetEarliestScheduling(start.Value, end.Value, setup.Value, cycle.Value);
        }
    }

    private static void EnsureOfferHasInputParameters(CapabilityRequirement requirement, I40Message message, OfferedCapability offer)
    {
        if (offer == null)
        {
            throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message));
        }

        // Accept both typed ActionModel and generic SubmodelElementCollection actions.
        foreach (var actionElement in offer.Actions)
        {
            if (actionElement is ActionModel actionModel)
            {
                // Presence of the collection is enough (can be empty).
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
                if (hasInputParameters)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message, missingFields: new[] { "Action.InputParameters" }));
    }

    private static ISubmodelElement EnsureListChildHasNoIdShort(ISubmodelElement element)
    {
        if (element is not IReferable referable)
        {
            return element;
        }

        if (string.IsNullOrEmpty(referable.IdShort))
        {
            return element;
        }

        if (element is SubmodelElementCollection smc)
        {
            var clone = new SubmodelElementCollection(string.Empty)
            {
                SemanticId = smc.SemanticId,
                Description = smc.Description,
                Qualifiers = smc.Qualifiers
            };

            var children = smc.Values ?? Array.Empty<ISubmodelElement>();
            foreach (var child in children)
            {
                clone.Add(child);
            }

            return clone;
        }

        if (element is SubmodelElementList list)
        {
            var clone = new SubmodelElementList(string.Empty)
            {
                SemanticId = list.SemanticId,
                Description = list.Description,
                Qualifiers = list.Qualifiers
            };

            foreach (var child in list)
            {
                clone.Add(child);
            }

            return clone;
        }

        return element;
    }

    private static bool TryMaterializeOfferedCapability(ISubmodelElement element, out OfferedCapability capability)
    {
        capability = null!;
        switch (element)
        {
            case OfferedCapability direct:
                capability = direct;
                return true;
            case SubmodelElementCollection collection when IsOfferedCapabilityCollection(collection):
                capability = CreateOfferedCapabilityFromCollection(collection);
                capability.IdShort = string.Empty;
                return true;
            default:
                return false;
        }
    }

    private static bool IsOfferedCapabilityCollection(SubmodelElementCollection collection)
    {
        if (collection == null)
        {
            return false;
        }

        if (string.Equals(collection.IdShort, "OfferedCapability", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Many CapabilitySequence entries intentionally clear IdShort. Detect them by contained properties.
        var children = collection.Values ?? Array.Empty<ISubmodelElement>();
        return children.OfType<ReferenceElement>().Any(r =>
                   string.Equals(r.IdShort, OfferedCapability.OfferedCapabilityReferenceIdShort, StringComparison.OrdinalIgnoreCase))
               || children.OfType<Property>().Any(p =>
                   string.Equals(p.IdShort, OfferedCapability.InstanceIdentifierIdShort, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildMissingOfferedCapabilityDetails(CapabilityRequirement requirement, I40Message? message, IEnumerable<string>? missingFields = null)
    {
        var sender = message?.Frame?.Sender?.Identification?.Id ?? string.Empty;
        var conv = message?.Frame?.ConversationId ?? string.Empty;
        var reqId = ExtractProperty(message, "RequirementId") ?? requirement?.RequirementId ?? string.Empty;
        var cap = ExtractProperty(message, "Capability") ?? requirement?.Capability ?? string.Empty;

        var elements = message?.InteractionElements?.ToList() ?? new List<ISubmodelElement>();
        var elementSummary = elements.Count == 0
            ? "<none>"
            : string.Join(", ", elements.Select(e => $"{e.GetType().Name}:{(e as ISubmodelElement)?.IdShort ?? ""}"));

        var capabilityPresent = message == null ? false : !string.IsNullOrWhiteSpace(ExtractProperty(message, "Capability"));
        var requirementPresent = message == null ? false : !string.IsNullOrWhiteSpace(ExtractProperty(message, "RequirementId"));
        var offerIdPresent = message == null ? false : !string.IsNullOrWhiteSpace(ExtractProperty(message, "OfferId"));
        var stationPresent = message == null ? false : !string.IsNullOrWhiteSpace(ExtractProperty(message, "Station"));
        var propertiesPresent = $"Capability={capabilityPresent}, RequirementId={requirementPresent}, OfferId={offerIdPresent}, Station={stationPresent}";

        var missing = missingFields == null ? "" : $" Missing=[{string.Join(", ", missingFields)}]";
        return $"CollectCapabilityOffer: Failed to extract valid OfferedCapability (expected element type OfferedCapability OR SubmodelElementCollection with IdShort='OfferedCapability'). Sender='{sender}' ConversationId='{conv}' Capability='{cap}' RequirementId='{reqId}'.{missing} PropertiesPresent=[{propertiesPresent}] InteractionElements=[{elementSummary}]";
    }

    private static TimeSpan? ParseTimeSpanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double ExtractDouble(object? value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s => ParseDouble(s),
            _ => ParseDouble(TryExtractString(value))
        };
    }

    private static string? ExtractProperty(I40Message? message, string idShort)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            if (element is Property prop && string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
            {
                return TryExtractString(prop.Value?.Value);
            }
        }

        return null;
    }

    private static string? TryExtractString(object? value)
    {
        if (value is string literal)
        {
            return literal;
        }

        return value?.ToString();
    }

    private static DateTime? ParseDateTimeUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}

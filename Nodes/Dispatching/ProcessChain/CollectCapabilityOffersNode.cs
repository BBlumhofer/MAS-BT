using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Messages;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Microsoft.Extensions.Logging;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class CollectCapabilityOfferNode : BTNode
{
    private readonly ConcurrentQueue<I40Message> _incoming = new();
    private readonly HashSet<string> _respondedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reissuedCfPs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processedOfferInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _reissueTimestamps = new();
    private bool _subscribed;
    private DateTime _startTime;
    private int _expectedModules;
    private HashSet<string>? _expectedModuleIds;
    private bool _drainedInbox;
    private bool _expectedRespondersBound;
    private readonly HashSet<string> _sequentiallyCompletedRequirements = new(StringComparer.OrdinalIgnoreCase);

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
            _expectedRespondersBound = false;
            TryBindExpectedResponders();

            if (!_expectedRespondersBound)
            {
                var state = Context.Get<DispatchingState>("DispatchingState");
                _expectedModules = state?.Modules.Count ?? 0;
            }

            Logger.LogInformation("CollectCapabilityOffer: waiting for responses from {Count} modules", _expectedModules);
            Logger.LogInformation("CollectCapabilityOffer: started at {StartUtc:o} TimeoutSeconds={TimeoutSeconds}", _startTime, TimeoutSeconds);
        }
        else if (!_expectedRespondersBound)
        {
            TryBindExpectedResponders();
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
                Logger.LogInformation("CollectCapabilityOffer: timeout after {Timeout}s", TimeoutSeconds);

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

                Logger.LogInformation(
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
        _processedOfferInstanceIds.Clear();
        _subscribed = false;
        _expectedModules = 0;
        _expectedModuleIds = null;
        _drainedInbox = false;
        _expectedRespondersBound = false;
        _sequentiallyCompletedRequirements.Clear();
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
            topic = TopicHelper.BuildNamespaceTopic(Context, "ProcessChain/Request");
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
            // Respect recent reissue cooldown to avoid repeated CfP floods when modules re-register frequently.
            const int ReissueCooldownSeconds = 15;
            if (_reissueTimestamps.TryGetValue(expected, out var lastReissue))
            {
                if ((DateTime.UtcNow - lastReissue).TotalSeconds < ReissueCooldownSeconds)
                {
                    continue;
                }
            }

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
            _reissueTimestamps[expected] = DateTime.UtcNow;
            Logger.LogInformation("CollectCapabilityOffer: re-issued {Count} CfP(s) to late-registered module {Module} on {Topic}", cfps.Count, expected, topic);
        }
    }

    private void ProcessMessage(ProcessChainNegotiationContext ctx, I40Message message)
    {
        var requirement = ResolveRequirement(ctx, message);
        if (requirement == null)
        {
            Logger.LogWarning("CollectCapabilityOffer: received message that does not match any requirement. Conv={Conv} Type={Type} Sender={Sender}",
                message?.Frame?.ConversationId ?? "<none>",
                message?.Frame?.Type ?? "<none>",
                message?.Frame?.Sender?.Identification?.Id ?? "<none>");

            // Dump interaction elements for debugging
            if (message?.InteractionElements != null)
            {
                try
                {
                    var elems = string.Join(", ", message.InteractionElements.Select(e => (e as ISubmodelElement)?.IdShort ?? e.GetType().Name));
                    Logger.LogDebug("CollectCapabilityOffer: InteractionElements for conv {Conv}: {Elems}", message.Frame?.ConversationId ?? "<none>", elems);
                }
                catch { }
            }
            return;
        }

        var messageType = message.Frame?.Type ?? string.Empty;
        // Support message types with semantic subtypes (e.g. "proposal/ManufacturingSequence").
        var messageBaseType = messageType.Split('/')[0];
        var sender = message.Frame?.Sender?.Identification?.Id;
        var senderModuleId = NormalizeModuleId(sender);
        var now = DateTime.UtcNow;
        var sinceStartMs = (_startTime == default) ? 0.0 : (now - _startTime).TotalMilliseconds;
        Logger.LogInformation("CollectCapabilityOffer: processing incoming {Type} from {SenderModule} at {Now:o} (+{SinceStart:F1}ms since start)", messageType, senderModuleId ?? "<unknown>", now, sinceStartMs);
        if (string.Equals(messageBaseType, I40MessageTypes.PROPOSAL, StringComparison.OrdinalIgnoreCase))
        {
            var requestType = Context.Get<string>("ProcessChain.RequestType") ?? string.Empty;
            var isManufacturing = string.Equals(requestType, "ManufacturingSequence", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(senderModuleId))
            {
                _respondedModules.Add(senderModuleId);
            }

            // De-dup: callbacks/conversation routing can deliver the same proposal more than once.
            // Prefer explicit top-level OfferId, otherwise try to extract nested InstanceIdentifier from the offered capability.
            var topLevelOfferId = ExtractProperty(message, "OfferId");
            var nestedInstanceId = ExtractInstanceIdentifierFromMessage(message);
            var incomingOfferId = !string.IsNullOrWhiteSpace(topLevelOfferId) ? topLevelOfferId : nestedInstanceId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(incomingOfferId))
            {
                // Already recorded under this requirement?
                var existsInRequirement = requirement.CapabilityOffers.Any(o =>
                    string.Equals(o.InstanceIdentifier?.GetText() ?? string.Empty, incomingOfferId, StringComparison.OrdinalIgnoreCase));

                if (existsInRequirement || _processedOfferInstanceIds.Contains(incomingOfferId))
                {
                    Logger.LogDebug("CollectCapabilityOffer: ignored duplicate offer {OfferId} (Conv={Conv}) for capability {Capability}", incomingOfferId, message.Frame?.ConversationId ?? "<none>", requirement.Capability);
                    return;
                }

                // Mark as processed to avoid duplicates across ticks/callbacks
                _processedOfferInstanceIds.Add(incomingOfferId);
            }

            try
            {
                if (isManufacturing)
                {
                    var sequences = BuildOfferedCapabilitySequences(requirement, message);
                    foreach (var sequence in sequences)
                    {
                        requirement.AddSequence(sequence);
                        Logger.LogInformation(
                            "CollectCapabilityOffer: recorded offered capability sequence (items={Count}) for capability {Capability} (received at {Now:o}, +{SinceStart:F1}ms)",
                            sequence.GetCapabilities().Count(),
                            requirement.Capability,
                            now,
                            sinceStartMs);
                    }
                    UpdateManufacturingSequenceSnapshot(ctx);

                    // Populate legacy offers list with the first element (for non-manufacturing consumers).
                    var first = sequences.SelectMany(s => s.GetCapabilities()).FirstOrDefault();
                    if (first != null)
                    {
                        requirement.AddOffer(first);
                    }

                    TryNotifySequentialRequirementSatisfied(requirement);
                }
                else
                {
                    var offers = BuildCapabilityOffers(requirement, message);
                    foreach (var offer in offers)
                    {
                        if (offer == null)
                        {
                            continue;
                        }
                        requirement.AddOffer(offer);
                        var offerId = offer.InstanceIdentifier.GetText() ?? "<unknown>";
                        Logger.LogInformation("CollectCapabilityOffer: recorded offer {OfferId} for capability {Capability} (received at {Now:o}, +{SinceStart:F1}ms)", offerId, requirement.Capability, now, sinceStartMs);
                    }

                    TryNotifySequentialRequirementSatisfied(requirement);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "CollectCapabilityOffer: invalid proposal received (cannot extract offer payload) Conv={Conv} Type={Type}", message?.Frame?.ConversationId ?? "<none>", message?.Frame?.Type ?? "<none>");
                // Log raw interaction elements for investigation
                try
                {
                    if (message?.InteractionElements != null)
                    {
                        foreach (var el in message.InteractionElements)
                        {
                            Logger.LogDebug("CollectCapabilityOffer: Element Type={Type} IdShort={IdShort}", el.GetType().Name, (el as ISubmodelElement)?.IdShort ?? "");
                        }
                    }
                }
                catch { }
                throw;
            }
        }
        else if (string.Equals(messageBaseType, I40MessageTypes.REFUSE_PROPOSAL, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(messageBaseType, I40MessageTypes.REFUSAL, StringComparison.OrdinalIgnoreCase))
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

    private void TryBindExpectedResponders()
    {
        if (_expectedRespondersBound)
        {
            return;
        }

        List<string>? expected = null;
        try
        {
            expected = Context.Get<List<string>>("ProcessChain.ExpectedOfferResponders");
        }
        catch
        {
            try
            {
                expected = Context.Get<List<string>>("ProcessChain.ExpectedOfferResponders".ToLowerInvariant());
            }
            catch
            {
                expected = null;
            }
        }

        if (expected == null || expected.Count == 0)
        {
            return;
        }

        _expectedModuleIds = expected
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeModuleId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_expectedModuleIds.Count == 0)
        {
            return;
        }

        _expectedModules = _expectedModuleIds.Count;
        _expectedRespondersBound = true;
        Logger.LogInformation("CollectCapabilityOffer: bound expected responders ({Count} modules)", _expectedModules);
    }

    private void TryNotifySequentialRequirementSatisfied(CapabilityRequirement requirement)
    {
        if (requirement == null)
        {
            return;
        }

        if (requirement.CapabilityOffers.Count == 0 && requirement.OfferedCapabilitySequences.Count == 0)
        {
            return;
        }

        var requirementId = requirement.RequirementId;
        if (string.IsNullOrWhiteSpace(requirementId))
        {
            return;
        }

        if (_sequentiallyCompletedRequirements.Contains(requirementId))
        {
            return;
        }

        SequentialRequirementCoordinator? coordinator = null;
        try
        {
            coordinator = Context.Get<SequentialRequirementCoordinator>("ProcessChain.SequentialCoordinator");
        }
        catch
        {
            coordinator = null;
        }

        if (coordinator == null)
        {
            return;
        }

        if (_sequentiallyCompletedRequirements.Add(requirementId))
        {
            coordinator.MarkRequirementCompleted(requirementId);
            Logger.LogInformation("CollectCapabilityOffer: sequential requirement {RequirementId} satisfied", requirementId);
        }
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

    private static IList<ManufacturingOfferedCapabilitySequence> BuildOfferedCapabilitySequences(CapabilityRequirement requirement, I40Message message)
    {
        var sequences = ExtractOfferedCapabilitySequences(message);
        if (sequences.Count == 0)
        {
            // Backward compatibility: allow a proposal that only contains a single OfferedCapability.
            var offers = ExtractOfferedCapabilities(message);
            if (offers.Count == 0)
            {
                var details = BuildMissingOfferedCapabilityDetails(requirement, message);
                throw new InvalidOperationException(details);
            }

            var fallback = new ManufacturingOfferedCapabilitySequence();
            foreach (var offer in offers)
            {
                EnsureSequenceOfferDefaults(requirement, message, offer, allowOfferIdFallback: offers.Count == 1);
                EnsureOfferHasInputParameters(requirement, message, offer);
                fallback.AddCapability(offer);
            }

            sequences.Add(fallback);
            return sequences;
        }

        foreach (var seq in sequences)
        {
            var offers = seq.GetCapabilities().ToList();
            if (offers.Count == 0)
            {
                var details = BuildMissingOfferedCapabilityDetails(requirement, message);
                throw new InvalidOperationException(details);
            }

            foreach (var offer in offers)
            {
                EnsureSequenceOfferDefaults(requirement, message, offer, allowOfferIdFallback: offers.Count == 1);
                EnsureOfferHasInputParameters(requirement, message, offer);
            }
        }

        return sequences;
    }

    private static void EnsureSequenceOfferDefaults(CapabilityRequirement requirement, I40Message message, OfferedCapability offer, bool allowOfferIdFallback)
    {
        var offerId = offer.InstanceIdentifier.GetText();
        if (string.IsNullOrWhiteSpace(offerId))
        {
            if (!allowOfferIdFallback)
            {
                throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message, missingFields: new[] { "OfferedCapability.InstanceIdentifier" }));
            }

            offerId = ExtractProperty(message, "OfferId");
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message, missingFields: new[] { "OfferedCapability.InstanceIdentifier / OfferId" }));
            }

            offer.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
        }

        // Apply standard defaults.
        EnsureOfferDefaults(requirement, message, offer);
    }

    private static List<ManufacturingOfferedCapabilitySequence> ExtractOfferedCapabilitySequences(I40Message message)
    {
        var sequences = new List<ManufacturingOfferedCapabilitySequence>();
        if (message?.InteractionElements == null)
        {
            return sequences;
        }

        foreach (var element in message.InteractionElements)
        {
            CollectOfferedCapabilitySequences(element, sequences);
        }

        return sequences;
    }

    private static void CollectOfferedCapabilitySequences(ISubmodelElement element, IList<ManufacturingOfferedCapabilitySequence> sink)
    {
        if (element == null)
        {
            return;
        }

        switch (element)
        {
            case ManufacturingOfferedCapabilitySequence typed:
                sink.Add(typed);
                break;
            case SubmodelElementList list when string.Equals(list.IdShort, "OfferedCapabilitySequence", StringComparison.OrdinalIgnoreCase):
                sink.Add(MaterializeOfferedCapabilitySequence(list));
                break;
            case SubmodelElementCollection collection when collection.Values != null:
                foreach (var child in collection.Values)
                {
                    CollectOfferedCapabilitySequences(child, sink);
                }
                break;
            case SubmodelElementList list:
                foreach (var child in list)
                {
                    CollectOfferedCapabilitySequences(child, sink);
                }
                break;
        }
    }

    private static ManufacturingOfferedCapabilitySequence MaterializeOfferedCapabilitySequence(SubmodelElementList list)
    {
        var seq = new ManufacturingOfferedCapabilitySequence(list?.IdShort ?? "OfferedCapabilitySequence");
        if (list == null)
        {
            return seq;
        }

        foreach (var item in list)
        {
            if (TryMaterializeOfferedCapability(item, out var offer))
            {
                seq.AddCapability(offer);
            }
        }

        return seq;
    }

    private void UpdateManufacturingSequenceSnapshot(ProcessChainNegotiationContext ctx)
    {
        if (ctx == null)
        {
            return;
        }

        var productId = ctx.ProductId;
        if (string.IsNullOrWhiteSpace(productId))
        {
            return;
        }

        var snapshot = BuildManufacturingSequenceSnapshot(ctx);
        if (snapshot == null)
        {
            return;
        }

        Dictionary<string, ManufacturingSequence>? index = null;
        try
        {
            index = Context.Get<Dictionary<string, ManufacturingSequence>>("ManufacturingSequence.ByProduct");
        }
        catch
        {
            // Key not present yet - create a new container below.
        }

        index ??= new Dictionary<string, ManufacturingSequence>(StringComparer.OrdinalIgnoreCase);
        index[productId] = snapshot;

        Context.Set("ManufacturingSequence.ByProduct", index);
        Context.Set("ManufacturingSequence.Result", snapshot);
        Context.Set("ManufacturingSequence.Success", ctx.HasCompleteProcessChain);
    }

    private static ManufacturingSequence BuildManufacturingSequenceSnapshot(ProcessChainNegotiationContext ctx)
    {
        var sequence = new ManufacturingSequence();
        var requirementIndex = 0;

        foreach (var requirement in ctx.Requirements)
        {
            var requiredCapability = new ManufacturingRequiredCapability($"RequiredCapability_{++requirementIndex}");
            requiredCapability.SetInstanceIdentifier(requirement.RequirementId);

            var requestedReference = CloneReference(requirement.RequestedCapabilityReference);
            if (requestedReference != null)
            {
                requiredCapability.SetRequiredCapabilityReference(requestedReference);
            }

            if (requirement.OfferedCapabilitySequences.Count > 0)
            {
                foreach (var offeredSequence in requirement.OfferedCapabilitySequences)
                {
                    requiredCapability.AddSequence(offeredSequence);
                }
            }
            else if (requirement.CapabilityOffers.Count > 0)
            {
                var fallback = new ManufacturingOfferedCapabilitySequence();
                foreach (var offer in requirement.CapabilityOffers)
                {
                    fallback.AddCapability(offer);
                }
                requiredCapability.AddSequence(fallback);
            }

            sequence.AddRequiredCapability(requiredCapability);
        }

        return sequence;
    }

    private static Reference? CloneReference(Reference? source)
    {
        if (source == null)
        {
            return null;
        }

        var keys = source.Keys?
            .Select(k => (IKey)new Key(k.Type, k.Value))
            .ToList();

        if (keys == null || keys.Count == 0)
        {
            return null;
        }

        return new Reference(keys)
        {
            Type = source.Type
        };
    }

    private static void EnsureOfferDefaults(CapabilityRequirement requirement, I40Message message, OfferedCapability offer)
    {
        var offerId = offer.InstanceIdentifier.GetText();
        if (string.IsNullOrWhiteSpace(offerId))
        {
            offerId = ExtractProperty(message, "OfferId");
            if (string.IsNullOrWhiteSpace(offerId))
            {
                throw new InvalidOperationException(BuildMissingOfferedCapabilityDetails(requirement, message, missingFields: new[] { "OfferedCapability.InstanceIdentifier / OfferId" }));
            }
            offer.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
        }

        var station = offer.Station.GetText();
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

        var cost = AasValueUnwrap.UnwrapToDouble(offer.Cost.Value) ?? 0;
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
                    offer.InstanceIdentifier.Value = new PropertyValue<string>(prop.GetText() ?? string.Empty);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.StationIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.Station.Value = new PropertyValue<string>(prop.GetText() ?? string.Empty);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.MatchingScoreIdShort, StringComparison.OrdinalIgnoreCase):
                    var score = AasValueUnwrap.UnwrapToDouble(prop.Value) ?? 0;
                    offer.MatchingScore.Value = new PropertyValue<double>(score);
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.CostIdShort, StringComparison.OrdinalIgnoreCase):
                    var detectedCost = AasValueUnwrap.UnwrapToDouble(prop.Value) ?? 0;
                    if (detectedCost > 0)
                    {
                        offer.SetCost(detectedCost);
                    }
                    break;
                case Property prop when string.Equals(prop.IdShort, OfferedCapability.SequencePlacementIdShort, StringComparison.OrdinalIgnoreCase):
                    offer.SequencePlacement.Value = new PropertyValue<string>(prop.GetText() ?? string.Empty);
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
            var value = element.GetText();
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
                return prop.GetText();
            }
        }

        return null;
    }

    private static string? ExtractInstanceIdentifierFromMessage(I40Message? message)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            var found = FindInstanceIdentifierInElement(element);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindInstanceIdentifierInElement(ISubmodelElement? element)
    {
        if (element == null) return null;

        if (element is Property prop && string.Equals(prop.IdShort, "InstanceIdentifier", StringComparison.OrdinalIgnoreCase))
        {
            return prop.GetText();
        }

        if (element is SubmodelElementCollection coll)
        {
            var children = coll.Values ?? Array.Empty<ISubmodelElement>();
            foreach (var child in children)
            {
                var found = FindInstanceIdentifierInElement(child);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }

        if (element is SubmodelElementList list)
        {
            foreach (var child in list)
            {
                var found = FindInstanceIdentifierInElement(child);
                if (!string.IsNullOrWhiteSpace(found)) return found;
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

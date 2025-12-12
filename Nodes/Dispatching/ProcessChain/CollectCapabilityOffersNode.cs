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

public class CollectCapabilityOffersNode : BTNode
{
    private readonly ConcurrentQueue<I40Message> _incoming = new();
    private readonly HashSet<string> _respondedModules = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscribed;
    private DateTime _startTime;
    private int _expectedModules;

    public int TimeoutSeconds { get; set; } = 5;

    public CollectCapabilityOffersNode() : base("CollectCapabilityOffers") { }

    public override Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        var ctx = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (client == null || ctx == null)
        {
            Logger.LogError("CollectCapabilityOffers: missing client or context");
            return Task.FromResult(NodeStatus.Failure);
        }

        if (!_subscribed)
        {
            client.OnConversation(ctx.ConversationId, msg => _incoming.Enqueue(msg));
            _startTime = DateTime.UtcNow;
            _subscribed = true;

            var state = Context.Get<DispatchingState>("DispatchingState");
            _expectedModules = state?.Modules.Count ?? 0;
            Logger.LogInformation("CollectCapabilityOffers: waiting for responses from {Count} modules", _expectedModules);
        }

        while (_incoming.TryDequeue(out var message))
        {
            ProcessMessage(ctx, message);
        }

        var complete = ctx.Requirements.Count > 0 && ctx.Requirements.All(r => r.CapabilityOffers.Count > 0);
        var allModulesResponded = _expectedModules == 0 || _respondedModules.Count >= _expectedModules;
        var timedOut = (DateTime.UtcNow - _startTime).TotalSeconds >= TimeoutSeconds;

        if (complete || allModulesResponded || timedOut)
        {
            if (timedOut)
            {
                Logger.LogWarning("CollectCapabilityOffers: timeout after {Timeout}s", TimeoutSeconds);
            }

            Context.Set("ProcessChain.Negotiation", ctx);
            return Task.FromResult(NodeStatus.Success);
        }

        return Task.FromResult(NodeStatus.Running);
    }

    public override Task OnReset()
    {
        _incoming.Clear();
        _respondedModules.Clear();
        _subscribed = false;
        _expectedModules = 0;
        return Task.CompletedTask;
    }

    private void ProcessMessage(ProcessChainNegotiationContext ctx, I40Message message)
    {
        var sender = message.Frame?.Sender?.Identification?.Id;
        if (!string.IsNullOrWhiteSpace(sender))
        {
            _respondedModules.Add(sender!);
        }

        var requirement = ResolveRequirement(ctx, message);
        if (requirement == null)
        {
            Logger.LogDebug("CollectCapabilityOffers: received message that does not match any requirement");
            return;
        }

        var messageType = message.Frame?.Type ?? string.Empty;
        if (string.Equals(messageType, I40MessageTypes.PROPOSAL, StringComparison.OrdinalIgnoreCase))
        {
            var offer = BuildCapabilityOffer(requirement, message);
            requirement.AddOffer(offer);
            Logger.LogInformation("CollectCapabilityOffers: recorded offer {OfferId} for capability {Capability}", offer.InstanceIdentifier.Value.Value, requirement.Capability);
        }
        else if (string.Equals(messageType, I40MessageTypes.REFUSE_PROPOSAL, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(messageType, I40MessageTypes.REFUSAL, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("CollectCapabilityOffers: module {Sender} refused requirement {Requirement}", sender, requirement.RequirementId);
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

    private static OfferedCapability BuildCapabilityOffer(CapabilityRequirement requirement, I40Message message)
    {
        var offer = new OfferedCapability($"Offer_{requirement.Capability}_{requirement.CapabilityOffers.Count + 1}");
        var offerId = ExtractProperty(message, "OfferId") ?? Guid.NewGuid().ToString();
        offer.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
        var station = message.Frame?.Sender?.Identification?.Id ?? string.Empty;
        offer.Station.Value = new PropertyValue<string>(station);

        var startUtc = ParseDateTimeUtc(ExtractProperty(message, "EarliestStartUtc")) ?? DateTime.UtcNow.AddMinutes(1);
        var cycle = ParseDouble(ExtractProperty(message, "CycleTimeMinutes"));
        var setup = ParseDouble(ExtractProperty(message, "SetupTimeMinutes"));
        var cycleTime = TimeSpan.FromMinutes(cycle > 0 ? cycle : 1);
        var setupTime = TimeSpan.FromMinutes(setup >= 0 ? setup : 0);
        var endUtc = startUtc.Add(cycleTime);
        offer.SetEarliestScheduling(startUtc, endUtc, setupTime, cycleTime);

        var action = new ActionModel(
            idShort: $"Action_{requirement.Capability}",
            actionTitle: requirement.Capability,
            status: ActionStatusEnum.PLANNED,
            inputParameters: null,
            finalResultData: null,
            preconditions: null,
            skillReference: null,
            machineName: station);
        offer.AddAction(action);

        return offer;
    }

    private static string? ExtractProperty(I40Message message, string idShort)
    {
        if (message.InteractionElements == null)
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

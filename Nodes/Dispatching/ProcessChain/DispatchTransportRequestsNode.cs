using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

/// <summary>
/// Sends transport requests for each storage constraint when handling ManufacturingSequence requests.
/// </summary>
public class DispatchTransportRequestsNode : BTNode
{
    public double ResponseTimeoutSeconds { get; set; } = 15;

    public DispatchTransportRequestsNode() : base("DispatchTransportRequests")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var mode = Context.Get<string>("ProcessChain.RequestType") ?? "ProcessChain";
        if (!mode.Equals("ManufacturingSequence", StringComparison.OrdinalIgnoreCase))
        {
            return NodeStatus.Success;
        }

        var negotiation = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (negotiation == null)
        {
            Logger.LogError("DispatchTransportRequests: negotiation context missing");
            return NodeStatus.Failure;
        }

        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("DispatchTransportRequests: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var topic = $"/{ns}/request/TransportPlan";
        var productId = string.IsNullOrWhiteSpace(negotiation.ProductId) ? negotiation.RequesterId : negotiation.ProductId;

        var targets = StorageConstraintHelper.FindStorageTargets(negotiation);
        if (targets.Count == 0)
        {
            Logger.LogInformation("DispatchTransportRequests: no storage constraints found, skipping transport dispatch");
            return NodeStatus.Success;
        }

        foreach (var target in targets)
        {
            var transportElement = BuildTransportRequestElement(productId, target.Requirement, target.TargetStation);
            var message = BuildTransportMessage(transportElement);
            try
            {
                await client.PublishAsync(message, topic).ConfigureAwait(false);
                Logger.LogInformation("DispatchTransportRequests: published transport request for requirement {RequirementId} to {Topic}",
                    target.Requirement.RequirementId,
                    topic);

                var response = await WaitForTransportResponseAsync(client, message.Frame?.ConversationId ?? string.Empty).ConfigureAwait(false);
                if (response == null)
                {
                    Logger.LogWarning("DispatchTransportRequests: timeout waiting for transport response (conversation={Conversation})", message.Frame?.ConversationId);
                    Context.Set("ProcessChain.TransportFailure", true);
                    return NodeStatus.Failure;
                }

                Logger.LogInformation("DispatchTransportRequests: received transport response type={Type}", response.Frame?.Type ?? "<unknown>");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "DispatchTransportRequests: error dispatching transport request");
                Context.Set("ProcessChain.TransportFailure", true);
                return NodeStatus.Failure;
            }
        }

        Context.Set("ProcessChain.TransportFailure", false);
        return NodeStatus.Success;
    }

    private TransportRequestMessage BuildTransportRequestElement(string productId, CapabilityRequirement requirement, string targetStation)
    {
        var element = new TransportRequestMessage();
        var instanceId = $"{requirement.RequirementId}";
        element.InstanceIdentifier.Value = new PropertyValue<string>(instanceId);
        element.OfferedCapabilityIdentifier.Value = new PropertyValue<string>(SelectOfferedCapabilityIdentifier(requirement));
        element.TransportGoalStation.Value = new PropertyValue<string>(targetStation ?? string.Empty);
        element.SetIdentifierType(TransportRequestMessage.IdentifierTypeEnum.ProductId);
        element.IdentifierValue.Value = new PropertyValue<string>(string.IsNullOrWhiteSpace(productId) ? requirement.RequirementId : productId);
        element.SetAmount(1);
        return element;
    }

    private static string SelectOfferedCapabilityIdentifier(CapabilityRequirement requirement)
    {
        var offer = requirement.CapabilityOffers.FirstOrDefault();
        if (offer?.InstanceIdentifier?.Value?.Value is string id && !string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        if (!string.IsNullOrWhiteSpace(requirement.RequirementId))
        {
            return requirement.RequirementId;
        }

        return requirement.Capability ?? "Capability";
    }

    private I40Message BuildTransportMessage(TransportRequestMessage element)
    {
        var convId = Guid.NewGuid().ToString();
        var builder = new I40MessageBuilder()
            .From(Context.AgentId, Context.AgentRole)
            .To("TransportService", "DispatchingAgent")
            .WithType("transportRequest")
            .WithConversationId(convId)
            .AddElement(element);

        return builder.Build();
    }

    private async Task<I40Message?> WaitForTransportResponseAsync(MessagingClient client, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Callback(I40Message msg)
        {
            try { tcs.TrySetResult(msg); } catch { }
        }

        client.OnConversation(conversationId, Callback);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, ResponseTimeoutSeconds));
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == tcs.Task ? tcs.Task.Result : null;
    }
}

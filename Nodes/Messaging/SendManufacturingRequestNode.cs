using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Sends a manufacturing-sequence request (RequiredCapabilities + ProductIdentification) to the dispatcher.
/// </summary>
public class SendManufacturingRequestNode : BTNode
{
    public string Topic { get; set; } = "/{Namespace}/ManufacturingSequenceRequest";
    public string MessageType { get; set; } = "requestManufacturingSequence";

    public SendManufacturingRequestNode() : base("SendManufacturingRequest")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("SendManufacturingRequest: MessagingClient missing or disconnected");
            return NodeStatus.Failure;
        }

        var capability = Context.Get<CapabilityDescriptionSubmodel>("CapabilityDescriptionSubmodel")
                        ?? Context.Get<CapabilityDescriptionSubmodel>("AAS.Submodel.CapabilityDescription");
        var productId = Context.Get<ProductIdentificationSubmodel>("ProductIdentificationSubmodel")
                        ?? Context.Get<ProductIdentificationSubmodel>("AAS.Submodel.ProductIdentification");

        if (capability == null || productId == null)
        {
            Logger.LogError("SendManufacturingRequest: Missing CapabilityDescription or ProductIdentification submodel");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? "phuket";
        var topic = ResolveTopic(ns);
        var conversationId = Guid.NewGuid().ToString();
        var messageType = string.IsNullOrWhiteSpace(MessageType) ? "requestManufacturingSequence" : MessageType;

        try
        {
            var interactionElements = new List<SubmodelElement>
            {
                capability.CapabilitySet,
                WrapSubmodel(productId, "ProductIdentification")
            };

            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To($"{ns}/DispatchingAgent", "DispatchingAgent")
                .WithType(messageType)
                .WithConversationId(conversationId)
                .AddElements(interactionElements);

            var message = builder.Build();
            await client.PublishAsync(message, topic).ConfigureAwait(false);

            Context.Set("ConversationId", conversationId);
            Context.Set("ManufacturingRequest.ConversationId", conversationId);
            Context.Set("ManufacturingRequest.Topic", topic);
            Logger.LogInformation("SendManufacturingRequest: Published ASK to {Topic} (ConversationId={ConversationId})", topic, conversationId);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendManufacturingRequest: Failed to send request");
            return NodeStatus.Failure;
        }
    }

    private string ResolveTopic(string ns)
    {
        var template = string.IsNullOrWhiteSpace(Topic) ? "/{Namespace}/ManufacturingSequenceRequest" : Topic;
        var resolved = ResolvePlaceholders(template.Replace("{Namespace}", ns));
        return resolved.StartsWith('/') ? resolved : "/" + resolved.TrimStart('/');
    }

    private static SubmodelElementCollection WrapSubmodel(Submodel submodel, string idShort)
    {
        var collection = new SubmodelElementCollection(idShort);
        if (submodel.SubmodelElements?.Values != null)
        {
            foreach (var element in submodel.SubmodelElements.Values)
            {
                collection.Add(element);
            }
        }

        return collection;
    }
}

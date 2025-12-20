using System;
using System.Collections.Generic;
using System.Linq;
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
    public string Topic { get; set; } = "/{Namespace}/ManufacturingSequence/Request";
    public string MessageType { get; set; } = I40MessageTypes.CALL_FOR_PROPOSAL;
    public I40MessageTypeSubtypes MessageSubtype { get; set; } = I40MessageTypeSubtypes.ManufacturingSequence;

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
        var processChainElement = ResolveProcessChainElement();

        if (capability == null || productId == null)
        {
            Logger.LogError("SendManufacturingRequest: Missing CapabilityDescription or ProductIdentification submodel");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? "phuket";
        var topic = ResolveTopic(ns);
        var conversationId = Guid.NewGuid().ToString();
        var messageType = string.IsNullOrWhiteSpace(MessageType)
            ? I40MessageTypes.CALL_FOR_PROPOSAL
            : MessageType.Trim();
        var messageSubtype = MessageSubtype;

        try
        {
            var interactionElements = new List<SubmodelElement>
            {
                capability.CapabilitySet,
                WrapSubmodel(productId, "ProductIdentification")
            };

            // If an AssetLocation submodel is available on the blackboard, include it in the request
            AasSharpClient.Models.AssetLocationSubmodel? assetLocation = null;
            try
            {
                assetLocation = Context.Get<AasSharpClient.Models.AssetLocationSubmodel>("AssetLocationSubmodel")
                                ?? Context.Get<AasSharpClient.Models.AssetLocationSubmodel>("AAS.Submodel.AssetLocation")
                                ?? Context.Get<AasSharpClient.Models.AssetLocationSubmodel>("AssetLocation");
            }
            catch
            {
                // ignore missing/typed entries
            }

            if (assetLocation != null)
            {
                interactionElements.Add(WrapSubmodel(assetLocation, "AssetLocation"));
                Logger.LogDebug("SendManufacturingRequest: attached AssetLocation to request");
            }

            // Log interaction elements for debugging to ensure AssetLocation presence
            try
            {
                Logger.LogInformation("SendManufacturingRequest: prepared {Count} interaction elements", interactionElements.Count);
                foreach (var el in interactionElements)
                {
                    switch (el)
                    {
                                case SubmodelElementCollection coll:
                                    Logger.LogInformation("  - Collection IdShort={IdShort} Values={ValuesCount}", coll.IdShort, coll.Values == null ? 0 : coll.Values.Count());
                                    break;
                        case Property prop:
                            Logger.LogInformation("  - Property IdShort={IdShort} Value={Value}", prop.IdShort, prop.Value);
                            break;
                        default:
                            Logger.LogInformation("  - Element Type={Type} IdShort={IdShort}", el.GetType().Name, (el as SubmodelElement)?.IdShort);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SendManufacturingRequest: failed to log interaction elements");
            }

            if (processChainElement != null)
            {
                interactionElements.Add(processChainElement);
                Logger.LogDebug("SendManufacturingRequest: attached ProcessChain element with idShort={IdShort}", processChainElement.IdShort);
            }
            else
            {
                Logger.LogWarning("SendManufacturingRequest: no ProcessChain element present on blackboard; request will not embed the process chain");
            }

            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To($"{ns}/DispatchingAgent", "DispatchingAgent")
                .WithConversationId(conversationId)
                .AddElements(interactionElements);

            if (messageType.Contains('/', StringComparison.Ordinal))
            {
                builder.WithType(messageType);
            }
            else
            {
                builder.WithType(messageType, messageSubtype);
            }

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
        var template = string.IsNullOrWhiteSpace(Topic) ? "/{Namespace}/ManufacturingSequence/Request" : Topic;
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

    private SubmodelElement? ResolveProcessChainElement()
    {
        try
        {
            if (Context.Has("ProcessChain.Result"))
            {
                var element = Context.Get<SubmodelElement>("ProcessChain.Result");
                if (element != null)
                {
                    return element;
                }
            }
        }
        catch
        {
            // ignore and fall through
        }

        try
        {
            if (Context.Has("ProcessChain.Submodel"))
            {
                var submodel = Context.Get<Submodel>("ProcessChain.Submodel");
                var candidate = submodel?.SubmodelElements?.Values?
                    .OfType<SubmodelElementCollection>()
                    .FirstOrDefault(e => string.Equals(e.IdShort, "ProcessChain", StringComparison.OrdinalIgnoreCase));
                if (candidate != null)
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}

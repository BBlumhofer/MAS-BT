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
/// Sends a process chain request (ASK) with RequiredCapability and ProductIdentification as interaction elements.
/// </summary>
public class SendProcessChainRequestNode : BTNode
{
    public int TimeoutSeconds { get; set; } = 30;

    public SendProcessChainRequestNode() : base("SendProcessChainRequest")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("SendProcessChainRequest: MessagingClient missing or disconnected");
            return NodeStatus.Failure;
        }

        var capability = Context.Get<CapabilityDescriptionSubmodel>("CapabilityDescriptionSubmodel")
                        ?? Context.Get<CapabilityDescriptionSubmodel>("AAS.Submodel.CapabilityDescription");
        var productId = Context.Get<ProductIdentificationSubmodel>("ProductIdentificationSubmodel")
                        ?? Context.Get<ProductIdentificationSubmodel>("AAS.Submodel.ProductIdentification");

        if (capability == null || productId == null)
        {
            Logger.LogError("SendProcessChainRequest: Missing CapabilityDescription or ProductIdentification submodel");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? "phuket";
        var topic = $"/{ns}/ManufacturingSequence/Request";

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
                                ?? Context.Get<AasSharpClient.Models.AssetLocationSubmodel>("AAS.Submodel.AssetLocation");
            }
            catch
            {
                // ignore missing/typed entries
            }

            if (assetLocation != null)
            {
                interactionElements.Add(WrapSubmodel(assetLocation, "AssetLocation"));
            }

            var convId = Guid.NewGuid().ToString();
            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To("Broadcast", "System")
                .WithType(I40MessageTypes.CALL_FOR_PROPOSAL, I40MessageTypeSubtypes.ProcessChain)
                .WithConversationId(convId)
                .AddElements(interactionElements);

            var message = builder.Build();

            // Log interaction elements for debugging to ensure AssetLocation presence
            try
            {
                Logger.LogInformation("SendProcessChainRequest: prepared {Count} interaction elements", interactionElements.Count);
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
                Logger.LogWarning(ex, "SendProcessChainRequest: failed to log interaction elements");
            }

            await client.PublishAsync(message, topic);
            Context.Set("ConversationId", convId);
            Logger.LogInformation("SendProcessChainRequest: Sent ASK to topic {Topic} with ConversationId {ConversationId}", topic, convId);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendProcessChainRequest: Failed to send request");
            return NodeStatus.Failure;
        }
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

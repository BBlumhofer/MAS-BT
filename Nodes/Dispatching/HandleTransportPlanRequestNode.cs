using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using AasSharpClient.Models.ProcessChain;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Dispatching
{
    public class HandleTransportPlanRequestNode : BTNode
    {
        public HandleTransportPlanRequestNode() : base("HandleTransportPlanRequest") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            var incoming = Context.Get<I40Message>("LastReceivedMessage");
            if (client == null || incoming == null)
            {
                Logger.LogWarning("HandleTransportPlanRequest: missing client or message");
                return NodeStatus.Failure;
            }

            I40Message incomingMessage = incoming;

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var topic = $"/{ns}/TransportPlan";
            var conversationId = incomingMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            var requesterId = incomingMessage.Frame?.Sender?.Identification?.Id ?? "Unknown";

            try
            {
                // Diagnostic logging: inspect incoming InteractionElements and children
                if (incomingMessage.InteractionElements != null)
                {
                    Logger.LogInformation("HandleTransportPlanRequest: incoming InteractionElements count={Count}", incomingMessage.InteractionElements.Count);
                    foreach (var el in incomingMessage.InteractionElements)
                    {
                        Logger.LogInformation("HandleTransportPlanRequest: element IdShort={Id} Type={Type} ToString={Str}", el?.IdShort, el?.GetType().FullName, el?.ToString());
                        if (el is SubmodelElementCollection coll)
                        {
                            foreach (var child in coll)
                            {
                                var childVal = "";
                                try { childVal = UnwrapValueFromElement(child); } catch { childVal = "<unavailable>"; }
                                //Logger.LogInformation("  Child: IdShort={Id} Type={Type} Value={Val}", child?.IdShort, child?.GetType().FullName, childVal);
                            }
                        }
                    }
                }

                // If the incoming message contains a SubmodelElementCollection as first interaction element,
                // parse it directly into a TransportRequestMessage and build the response from that.
                TransportRequestMessage? parsedRequest = null;
                if (incomingMessage.InteractionElements != null && incomingMessage.InteractionElements.Count > 0)
                {
                    parsedRequest = incomingMessage.InteractionElements.FirstOrDefault() as SubmodelElementCollection is SubmodelElementCollection smc
                        ? new TransportRequestMessage(smc)
                        : null;
                }

                OfferedCapability transportOffer;
                TransportRequestMessage responseElement;
                if (parsedRequest != null)
                {
                    responseElement = BuildResponseElement(parsedRequest, out transportOffer);
                }
                else
                {
                    responseElement = BuildResponseElement(incomingMessage, out transportOffer);
                }
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(requesterId, null)
                    .WithType(I40MessageTypes.CONSENT, I40MessageTypeSubtypes.TransportRequest)
                    .WithConversationId(conversationId)
                    .AddElement(responseElement);

                var response = builder.Build();
                var publishedAt = DateTimeOffset.UtcNow;
                await client.PublishAsync(response, topic).ConfigureAwait(false);
                Logger.LogInformation("HandleTransportPlanRequest: sent dummy response to {Topic} at {Timestamp:o} (conversationId={Conv}, requester={Requester})",
                    topic,
                    publishedAt,
                    conversationId,
                    requesterId);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleTransportPlanRequest: failed to send transport response");
                return NodeStatus.Failure;
            }
        }

        private TransportRequestMessage BuildResponseElement(I40Message incoming, out OfferedCapability transportOffer)
        {
            // Try to find an existing SubmodelElementCollection in the incoming message to construct the request from
            SubmodelElementCollection? incomingCollection = null;
            if (incoming.InteractionElements != null)
            {
                // Prefer the first element if it already is a SubmodelElementCollection (common case)
                incomingCollection = incoming.InteractionElements.FirstOrDefault() as SubmodelElementCollection
                                     ?? incoming.InteractionElements.OfType<SubmodelElementCollection>().FirstOrDefault();
            }

            TransportRequestMessage req;
            if (incomingCollection != null)
            {
                req = new TransportRequestMessage(incomingCollection);
            }
            else
            {
                // fallback: construct from extracted values
                var goal = ExtractValue(incoming, "TransportGoalStation") ?? "Unknown";
                var identifierValue = ExtractValue(incoming, "IdentifierValue") ??  "Unknown";
                var identifierType = ExtractValue(incoming, "IdentifierType") ?? "Unknown";
                var amount = ExtractValue(incoming, "Amount") ?? "1";

                req = new TransportRequestMessage("TransportRequest");
                // Instance identifier - use a generated id
                req.InstanceIdentifier.Value = new PropertyValue<string>($"transportreq_{Guid.NewGuid():N}");
                req.TransportStartStation.Value = new PropertyValue<string>("Storage");
                req.TransportGoalStation.Value = new PropertyValue<string>(goal);
                req.IdentifierType.Value = new PropertyValue<string>(identifierType);
                req.IdentifierValue.Value = new PropertyValue<string>(identifierValue);
                if (int.TryParse(amount, out var amt)) req.SetAmount(amt);
            }

            // Ensure InstanceIdentifier exists
            if (req.InstanceIdentifier.IsNullOrWhiteSpace())
            {
                req.InstanceIdentifier.Value = new PropertyValue<string>($"transportreq_{Guid.NewGuid():N}");
            }

            // Build transport offer based on the resolved goal/identifier/type
            var resolvedGoal = req.TransportGoalStationText ?? "Unknown";
            var resolvedIdentifierValue = req.IdentifierValueText ?? "Unknown";
            var resolvedIdentifierType = req.IdentifierTypeText ?? "Unknown";

            transportOffer = BuildTransportOffer(resolvedGoal, resolvedIdentifierValue, resolvedIdentifierType);
            req.CapabilitiesSequence.AddCapability(transportOffer);

            return req;
        }

        private TransportRequestMessage BuildResponseElement(TransportRequestMessage incomingRequest, out OfferedCapability transportOffer)
        {
            var req = incomingRequest ?? new TransportRequestMessage("TransportRequest");

            // Ensure InstanceIdentifier exists
            if (req.InstanceIdentifier.IsNullOrWhiteSpace())
            {
                req.InstanceIdentifier.Value = new PropertyValue<string>($"transportreq_{Guid.NewGuid():N}");
            }

            var resolvedGoal = req.TransportGoalStationText ?? "Unknown";
            var resolvedIdentifierValue = req.IdentifierValueText ?? "Unknown";
            var resolvedIdentifierType = req.IdentifierTypeText ?? "Unknown";

            transportOffer = BuildTransportOffer(resolvedGoal, resolvedIdentifierValue, resolvedIdentifierType);

            // Avoid duplicating offers if already present
            req.CapabilitiesSequence.AddCapability(transportOffer);

            return req;
        }

        private OfferedCapability BuildTransportOffer(string goal, string identifierValue, string identifierType)
        {
            var offer = new OfferedCapability(string.Empty);
            var offerId = $"transport_{Guid.NewGuid():N}";
            offer.InstanceIdentifier.Value = new PropertyValue<string>(offerId);
            offer.Station.Value = new PropertyValue<string>(string.IsNullOrWhiteSpace(goal) ? "Storage" : goal);
            offer.MatchingScore.Value = new PropertyValue<double>(1.0);
            offer.SetEarliestScheduling(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), TimeSpan.Zero, TimeSpan.FromMinutes(1));
            offer.SetCost(0);

            var reference = new Reference(new[]
            {
                new Key(KeyType.GlobalReference, "Transport")
            })
            {
                Type = ReferenceType.ExternalReference
            };
            offer.OfferedCapabilityReference.Value = new ReferenceElementValue(reference);

            var action = new AasSharpClient.Models.Action(
                idShort: "Action_Transport",
                actionTitle: "Transport",
                status: ActionStatusEnum.OPEN,
                inputParameters: new InputParameters(),
                finalResultData: null,
                preconditions: null,
                skillReference: null,
                machineName: goal ?? "Transport");

            // Response requirement: return only the extracted identifier as a single input parameter.
            // Use the key format: "ProductID: https://..." (cosmetic normalization handled in message helper).
            var identifierKey = $"{reqKey(identifierType)}: {identifierValue}";
            action.InputParameters.SetParameter(identifierKey, string.Empty);
            offer.AddAction(action);

            return offer;
        }

        private static string reqKey(string identifierType)
        {
            if (string.IsNullOrWhiteSpace(identifierType)) return "Identifier";
            if (identifierType.EndsWith("Id", StringComparison.Ordinal) && identifierType.Length > 2)
            {
                return identifierType[..^2] + "ID";
            }
            return identifierType;
        }

        private static Property<string> CreateStringProperty(string idShort, string value)
        {
            return new Property<string>(idShort)
            {
                Value = new PropertyValue<string>(value ?? string.Empty)
            };
        }

        private static string? ExtractValue(I40Message? message, string idShort)
        {
            if (message?.InteractionElements == null)
            {
                return null;
            }

            foreach (var element in message.InteractionElements)
            {
                if (element is SubmodelElementCollection collection)
                {
                    var prop = collection.FirstOrDefault(e => string.Equals(e.IdShort, idShort, StringComparison.OrdinalIgnoreCase)) as Property<string>;
                    var data = prop.GetText();
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        return data;
                    }
                }
            }

            return null;
        }

        private static string UnwrapValueFromElement(ISubmodelElement element)
        {
            if (element == null) return "<null>";

            // If it's a Property, try to extract nested values
            if (element is Property prop)
            {
                try
                {
                    return AasValueUnwrap.UnwrapToString(prop.Value) ?? "<null>";
                }
                catch
                {
                    return "<error>";
                }
            }

            // For other element types, fallback to ToString()
            return element.ToString() ?? "<null>";
        }
    }
}

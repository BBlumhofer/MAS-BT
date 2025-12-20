using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using AasSharpClient.Models.ManufacturingSequence;
using AasSharpClient.Models.ProcessChain;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using AasSharpClient.Tools;
using ActionModel = AasSharpClient.Models.Action;

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
            var responseTopic = $"/{ns}/TransportPlan/Response";
            var conversationId = incomingMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            var requesterId = incomingMessage.Frame?.Sender?.Identification?.Id ?? "Unknown";

            try
            {
                // Diagnostic logging: inspect incoming InteractionElements and children
                try { Console.WriteLine($"[DEBUG] HandleTransportPlanRequest: invoked conversationId={conversationId} incomingElements={incomingMessage.InteractionElements?.Count ?? 0}"); } catch {}
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
                SubmodelElementCollection? incomingCollection = null;
                if (incomingMessage.InteractionElements != null && incomingMessage.InteractionElements.Count > 0)
                {
                    // direct cast when the element already is a collection
                    incomingCollection = incomingMessage.InteractionElements.FirstOrDefault() as SubmodelElementCollection
                                         ?? incomingMessage.InteractionElements.OfType<SubmodelElementCollection>().FirstOrDefault();

                    // Only accept a SubmodelElementCollection that is already present in the message.
                    // Do NOT attempt to (re)serialize or encode message elements here.

                        if (incomingCollection != null)
                        {
                            parsedRequest = new TransportRequestMessage(incomingCollection);
                        }
                }
                    // Strict mode: only accept requests that contain a SubmodelElementCollection
                    if (parsedRequest == null)
                    {
                        // Publish a refusal explaining that the payload is not acceptable
                        var senderId = string.IsNullOrWhiteSpace(Context.AgentId) ? "DispatchingAgent" : Context.AgentId;
                        var senderRole = string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole;
                        var receiverId = requesterId;
                        var receiverRole = incomingMessage.Frame?.Sender?.Role?.Name ?? "Unknown";
                        var refusalReason = "Invalid payload: expected SubmodelElementCollection in interaction elements";
                        var failureDetail = "Dispatching agent requires a SubmodelElementCollection as the first interaction element. No automatic reserialization is performed.";

                        var refusal = new AasSharpClient.Models.Messages.PlanningRefusalMessage(
                            senderId,
                            senderRole,
                            receiverId,
                            receiverRole,
                            conversationId,
                            refusalReason,
                            failureDetail);

                        await refusal.PublishAsync(client, responseTopic).ConfigureAwait(false);
                        Logger.LogInformation("HandleTransportPlanRequest: published refusal to {Topic} (conv={Conv})", responseTopic, conversationId);

                        return NodeStatus.Failure;
                    }

                    OfferedCapability transportOffer;
                    var responseElement = BuildResponseElement(parsedRequest, out transportOffer);
                EnsureTransportStartStation(responseElement, incomingMessage);
                // reuse or produce distinct names to avoid shadowing earlier locals
                var respSenderId = string.IsNullOrWhiteSpace(Context.AgentId) ? "DispatchingAgent" : Context.AgentId;
                var respSenderRole = string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole;
                var respReceiverRole = incomingMessage.Frame?.Sender?.Role?.Name;

                var responseMsg = new AasSharpClient.Models.Messages.TransportPlanResponseMessage(
                    respSenderId,
                    respSenderRole,
                    requesterId,
                    respReceiverRole,
                    conversationId,
                    responseElement);

                var publishedAt = DateTimeOffset.UtcNow;
                await responseMsg.PublishAsync(client, responseTopic, respSenderId, respSenderRole, requesterId, respReceiverRole, conversationId).ConfigureAwait(false);
                Logger.LogInformation("HandleTransportPlanRequest: sent dummy response to {Topic} at {Timestamp:o} (conversationId={Conv}, requester={Requester})",
                    responseTopic,
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

            try { Console.WriteLine($"[DEBUG] HandleTransportPlanRequest: built transport offer id={transportOffer.InstanceIdentifier.GetText()} identifierKey={reqKey(resolvedIdentifierType)} identifierValue={resolvedIdentifierValue}"); } catch {}

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

            var action = new ActionModel(
                idShort: "Action_Transport",
                actionTitle: "Transport",
                status: ActionStatusEnum.OPEN,
                inputParameters: new InputParameters(),
                finalResultData: null,
                preconditions: null,
                skillReference: null,
                machineName: goal ?? "Transport");

            // Response requirement: return the extracted identifier as input parameter.
            // IMPORTANT: InputParameters keys become AAS idShorts; keep them valid (no ':' / spaces / URLs).
            // Therefore use Key='ProductID' (etc.) and store the identifier in the value.
            var identifierKey = reqKey(identifierType);
            action.InputParameters.SetParameter(identifierKey, identifierValue);
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

        private void EnsureTransportStartStation(TransportRequestMessage message, I40Message incomingMessage)
        {
            if (message == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(message.TransportStartStationText))
            {
                return;
            }

            var identifierType = message.IdentifierTypeText ?? string.Empty;
            if (!string.Equals(identifierType, TransportRequestMessage.IdentifierTypeEnum.ProductId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var productId = ResolveProductId(message, incomingMessage);
            if (string.IsNullOrWhiteSpace(productId))
            {
                return;
            }

            var sequence = GetManufacturingSequenceForProduct(productId);
            var machineName = FindLatestStorageMachine(sequence);
            if (string.IsNullOrWhiteSpace(machineName))
            {
                Logger.LogWarning("HandleTransportPlanRequest: no storage postcondition found for product {ProductId}", productId);
                return;
            }

            message.TransportStartStation.Value = new PropertyValue<string>(machineName);
            Logger.LogInformation("HandleTransportPlanRequest: resolved transport start station '{Station}' for product {ProductId}", machineName, productId);
        }

        private static string? ResolveProductId(TransportRequestMessage message, I40Message incomingMessage)
        {
            if (!string.IsNullOrWhiteSpace(message?.IdentifierValueText))
            {
                return message.IdentifierValueText;
            }

            return incomingMessage?.Frame?.ConversationId;
        }

        private ManufacturingSequence GetManufacturingSequenceForProduct(string productId)
        {
            Dictionary<string, ManufacturingSequence>? index = null;
            try
            {
                index = Context.Get<Dictionary<string, ManufacturingSequence>>("ManufacturingSequence.ByProduct");
            }
            catch
            {
                // Key not present yet.
            }

            if (index != null && index.TryGetValue(productId, out var sequence) && sequence != null)
            {
                return sequence;
            }

            throw new NotImplementedException($"Transport planning for product '{productId}' is not implemented.");
        }

        private static string? FindLatestStorageMachine(ManufacturingSequence sequence)
        {
            if (sequence == null)
            {
                return null;
            }

            var requirements = sequence.GetRequiredCapabilities().ToList();
            for (var reqIndex = requirements.Count - 1; reqIndex >= 0; reqIndex--)
            {
                var requirement = requirements[reqIndex];
                if (requirement == null)
                {
                    continue;
                }

                var sequences = requirement.GetSequences().ToList();
                for (var seqIndex = sequences.Count - 1; seqIndex >= 0; seqIndex--)
                {
                    var offeredSequence = sequences[seqIndex];
                    if (offeredSequence == null)
                    {
                        continue;
                    }

                    var capabilities = offeredSequence.GetCapabilities().ToList();
                    for (var capIndex = capabilities.Count - 1; capIndex >= 0; capIndex--)
                    {
                        var capability = capabilities[capIndex];
                        var machineName = TryFindStorageMachine(capability);
                        if (!string.IsNullOrWhiteSpace(machineName))
                        {
                            return machineName;
                        }
                    }
                }
            }

            return null;
        }

        private static string? TryFindStorageMachine(OfferedCapability capability)
        {
            if (capability?.Actions == null)
            {
                return null;
            }

            var actions = capability.Actions.OfType<ActionModel>().ToList();
            for (var actionIndex = actions.Count - 1; actionIndex >= 0; actionIndex--)
            {
                var action = actions[actionIndex];
                if (action == null)
                {
                    continue;
                }

                if (HasStoragePostcondition(action))
                {
                    return action.MachineName.GetText();
                }
            }

            return null;
        }

        private static bool HasStoragePostcondition(ActionModel action)
        {
            if (action?.Postconditions == null)
            {
                return false;
            }

            foreach (var post in action.Postconditions.OfType<StoragePostcondition>())
            {
                return true;
            }

            return false;
        }
    }
}

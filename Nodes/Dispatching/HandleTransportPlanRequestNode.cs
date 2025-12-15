using System;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
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

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var topic = $"/{ns}/TransportPlan";
            var conversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            var requesterId = incoming.Frame?.Sender?.Identification?.Id ?? "Unknown";

            try
            {
                var responseElement = BuildResponseElement(incoming, out var transportOffer);
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(requesterId, null)
                    .WithType(I40MessageTypes.CONSENT, I40MessageTypeSubtypes.TransportRequest)
                    .WithConversationId(conversationId)
                    .AddElement(responseElement);

                if (transportOffer != null)
                {
                    builder.AddElement(transportOffer);
                }

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

        private SubmodelElementCollection BuildResponseElement(I40Message incoming, out OfferedCapability transportOffer)
        {
            var element = new SubmodelElementCollection("TransportResponse");
            var goal = ExtractValue(incoming, "TransportGoalStation") ?? "Unknown";
            var identifierValue = ExtractValue(incoming, "IdentifierValue") ?? string.Empty;
            var identifierType = ExtractValue(incoming, "IdentifierType") ?? "ProductId";
            var amount = ExtractValue(incoming, "Amount") ?? "1";

            element.Add(CreateStringProperty("TransportStartStation", "Storage"));
            element.Add(CreateStringProperty("TransportGoalStation", goal));
            element.Add(CreateStringProperty("IdentifierType", identifierType));
            element.Add(CreateStringProperty("IdentifierValue", identifierValue));
            element.Add(CreateStringProperty("Amount", amount));

            transportOffer = BuildTransportOffer(goal, identifierValue, identifierType, amount);
            var sequence = new SubmodelElementList("CapabilitySequence");
            sequence.Add(transportOffer);
            element.Add(sequence);
            return element;
        }

        private OfferedCapability BuildTransportOffer(string goal, string identifierValue, string identifierType, string amountText)
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

            action.InputParameters.SetParameter("IdentifierType", identifierType ?? string.Empty);
            action.InputParameters.SetParameter("IdentifierValue", identifierValue ?? string.Empty);
            action.InputParameters.SetParameter("Amount", string.IsNullOrWhiteSpace(amountText) ? "1" : amountText);
            offer.AddAction(action);

            return offer;
        }

        private static Property<string> CreateStringProperty(string idShort, string value)
        {
            return new Property<string>(idShort)
            {
                Value = new PropertyValue<string>(value ?? string.Empty)
            };
        }

        private static string? ExtractValue(I40Message message, string idShort)
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
                    var raw = prop?.Value?.Value;
                    var data = raw?.ToString();
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        return data;
                    }
                }
            }

            return null;
        }
    }
}

using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Dispatching
{
    /// <summary>
    /// Stub implementation: respond to manufacturing sequence requests with a refusal until scheduling is implemented.
    /// </summary>
    public class HandleManufacturingSequenceRequestNode : BTNode
    {
        public HandleManufacturingSequenceRequestNode() : base("HandleManufacturingSequenceRequest") { }

        public override async Task<NodeStatus> Execute()
        {
            return await SendSimpleRefusal("/response/ManufacturingSequence", "ManufacturingSequence not implemented yet");
        }

        private async Task<NodeStatus> SendSimpleRefusal(string relativeTopic, string reason)
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            var incoming = Context.Get<I40Message>("LastReceivedMessage");
            if (client == null || incoming == null)
            {
                Logger.LogWarning("HandleManufacturingSequenceRequest: missing client or message");
                return NodeStatus.Failure;
            }

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            // Normalize to unified pattern: /{Namespace}/ManufacturingSequence/Response
            string topic;
            if (relativeTopic.Contains("ManufacturingSequence", StringComparison.OrdinalIgnoreCase))
            {
                topic = $"/{ns}/ManufacturingSequence/Response";
            }
            else
            {
                topic = relativeTopic.StartsWith("/") ? $"/{ns}{relativeTopic}" : $"/{ns}/{relativeTopic}";
            }
            var conversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            var requesterId = incoming.Frame?.Sender?.Identification?.Id ?? "Unknown";

            try
            {
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(requesterId, null)
                    .WithType(I40MessageTypes.REFUSAL)
                    .WithConversationId(conversationId);

                var payload = new Property<string>("Reason")
                {
                    Value = new PropertyValue<string>(reason)
                };
                builder.AddElement(payload);

                var response = builder.Build();
                await client.PublishAsync(response, topic);
                Logger.LogInformation("HandleManufacturingSequenceRequest: sent refusal to {Topic}", topic);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleManufacturingSequenceRequest: failed to send refusal");
                return NodeStatus.Failure;
            }
        }
    }
}

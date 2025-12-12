using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Messages;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Dispatching
{
    public class HandleProcessChainRequestNode : BTNode
    {
        public HandleProcessChainRequestNode() : base("HandleProcessChainRequest") { }

        public override async Task<NodeStatus> Execute()
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("HandleProcessChainRequest: MessagingClient unavailable");
                return NodeStatus.Failure;
            }

            var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
            Context.Set("DispatchingState", state);

            var incoming = Context.Get<I40Message>("LastReceivedMessage");
            if (incoming == null)
            {
                Logger.LogWarning("HandleProcessChainRequest: no incoming message");
                return NodeStatus.Failure;
            }

            var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
            var conversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString();
            var requesterId = incoming.Frame?.Sender?.Identification?.Id ?? "Unknown";
            Context.Set("ConversationId", conversationId);

            var requestedCaps = ExtractRequestedCapabilities(incoming).ToList();
            if (requestedCaps.Count == 0)
            {
                requestedCaps = state.Modules.SelectMany(m => m.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (requestedCaps.Count == 0)
            {
                requestedCaps.Add("GenericCapability");
            }

            var steps = new List<ProcessChainStep>();
            var hasCandidates = true;

            foreach (var cap in requestedCaps)
            {
                var candidates = state.FindModulesForCapability(cap).ToList();
                if (candidates.Count == 0)
                {
                    hasCandidates = false;
                }
                steps.Add(new ProcessChainStep
                {
                    Capability = cap,
                    CandidateModules = candidates
                });
            }

            var responseDto = new ProcessChainProposal
            {
                ProcessChainId = conversationId,
                Steps = steps
            };

            var messageType = hasCandidates ? I40MessageTypes.PROPOSAL : I40MessageTypes.REFUSE_PROPOSAL;
            var responseTopic = $"/{ns}/ProcessChain";

            try
            {
                var builder = new I40MessageBuilder()
                    .From(Context.AgentId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "DispatchingAgent" : Context.AgentRole)
                    .To(requesterId, null)
                    .WithType(messageType)
                    .WithConversationId(conversationId);

                var serialized = JsonSerializer.Serialize(responseDto);
                var payload = new Property<string>("ProcessChain")
                {
                    Value = new PropertyValue<string>(serialized)
                };
                builder.AddElement(payload);

                var response = builder.Build();
                await client.PublishAsync(response, responseTopic);
                Logger.LogInformation("HandleProcessChainRequest: sent {Type} with {StepCount} steps to {Topic}", messageType, steps.Count, responseTopic);
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleProcessChainRequest: failed to publish response");
                return NodeStatus.Failure;
            }
        }

        private IEnumerable<string> ExtractRequestedCapabilities(I40Message message)
        {
            if (message?.InteractionElements == null)
                yield break;

            foreach (var element in message.InteractionElements)
            {
                if (element is Property prop)
                {
                    var text = TryExtractString(prop.Value?.Value);
                    if (!string.IsNullOrWhiteSpace(text)) yield return text!;
                }
                else if (element is SubmodelElementCollection coll)
                {
                    foreach (var child in coll.Values)
                    {
                        if (child is Property childProp)
                        {
                            var text = TryExtractString(childProp.Value?.Value);
                            if (!string.IsNullOrWhiteSpace(text)) yield return text!;
                        }
                    }
                }
            }
        }

        private static string? TryExtractString(object? value)
        {
            if (value is string s) return s;
            return value?.ToString();
        }

    }
}

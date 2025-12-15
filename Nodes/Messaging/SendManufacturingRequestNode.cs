using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaSyx.Clients.AdminShell.Http;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Sends the ProcessChain stored in the product's Administration Shell to the namespace ManufacturingSequenceRequest topic
/// to trigger manufacturing sequence planning.
/// </summary>
public class SendManufacturingRequestNode : BTNode
{
    /// <summary>
    /// Blackboard key that contains the ProcessChain element (optional). Defaults to "ProcessChain.Result".
    /// When missing a ProcessChain will be loaded from the AAS via <see cref="ProcessChainSubmodelIdKey"/>.
    /// </summary>
    public string ProcessChainContextKey { get; set; } = "ProcessChain.Result";

    /// <summary>
    /// Blackboard key that contains the identifier (IRI) of the ProcessChain submodel inside the product shell.
    /// </summary>
    public string ProcessChainSubmodelIdKey { get; set; } = "ProcessChain.SubmodelId";

    /// <summary>
    /// Optional override for the submodel repository endpoint. When empty the value from config.AAS.SubmodelRepositoryEndpoint is used.
    /// </summary>
    public string SubmodelRepositoryEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Topic to publish the manufacturing request to. Supports {Namespace} placeholder.
    /// </summary>
    public string Topic { get; set; } = "/{Namespace}/ManufacturingSequenceRequest";

    /// <summary>
    /// Message type used inside the I40 frame.
    /// </summary>
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

        var element = await ResolveProcessChainElementAsync().ConfigureAwait(false);
        if (element == null)
        {
            Logger.LogError("SendManufacturingRequest: Unable to resolve ProcessChain from context or AAS");
            return NodeStatus.Failure;
        }

        var topic = ResolveTopic();
        var conversationId = Guid.NewGuid().ToString();
        var messageType = string.IsNullOrWhiteSpace(MessageType) ? "requestManufacturingSequence" : MessageType;

        try
        {
            var interactionElements = new List<SubmodelElement> { element };

            var builder = new I40MessageBuilder()
                .From(Context.AgentId, Context.AgentRole)
                .To(ResolveReceiver(), "DispatchingAgent")
                .WithType(messageType)
                .WithConversationId(conversationId)
                .AddElements(interactionElements);

            var message = builder.Build();
            await client.PublishAsync(message, topic).ConfigureAwait(false);

            Context.Set("ManufacturingRequest.ConversationId", conversationId);
            Context.Set("ManufacturingRequest.Topic", topic);
            Logger.LogInformation(
                "SendManufacturingRequest: Published ProcessChain to {Topic} (ConversationId={ConversationId})",
                topic,
                conversationId);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendManufacturingRequest: Failed to publish manufacturing request");
            return NodeStatus.Failure;
        }
    }

    private string ResolveTopic()
    {
        var ns = Context.Get<string>("config.Namespace")
                 ?? Context.Get<string>("Namespace")
                 ?? "phuket";
        var template = string.IsNullOrWhiteSpace(Topic) ? "/{Namespace}/ManufacturingSequenceRequest" : Topic;
        var resolved = ResolvePlaceholders(template.Replace("{Namespace}", ns));
        if (!resolved.StartsWith('/'))
        {
            resolved = "/" + resolved.TrimStart('/');
        }
        return resolved;
    }

    private string ResolveReceiver()
    {
        var ns = Context.Get<string>("config.Namespace")
                 ?? Context.Get<string>("Namespace")
                 ?? "phuket";
        return $"{ns}/DispatchingAgent";
    }

    private async Task<SubmodelElement?> ResolveProcessChainElementAsync()
    {
        var submodelId = Context.Get<string>(ProcessChainSubmodelIdKey);
        if (string.IsNullOrWhiteSpace(submodelId))
        {
            submodelId = Context.Get<string>("Submodel.LastUploadedId");
        }

        if (string.IsNullOrWhiteSpace(submodelId))
        {
            if (!string.IsNullOrWhiteSpace(ProcessChainContextKey)
                && Context.Get<object>(ProcessChainContextKey) is SubmodelElementCollection collection)
            {
                Logger.LogDebug("SendManufacturingRequest: Falling back to in-memory ProcessChain from context key {Key}", ProcessChainContextKey);
                return collection;
            }

            Logger.LogWarning("SendManufacturingRequest: No ProcessChain submodel id available in context");
            return null;
        }

        var endpoint = ResolveSubmodelRepositoryEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Logger.LogWarning("SendManufacturingRequest: No SubmodelRepositoryEndpoint configured");
            return null;
        }

        try
        {
            using var client = new SubmodelRepositoryHttpClient(new Uri(endpoint));
            var result = await client.RetrieveSubmodelAsync(new Identifier(submodelId)).ConfigureAwait(false);
            if (!result.Success || result.Entity is not ISubmodel submodel)
            {
                Logger.LogWarning("SendManufacturingRequest: Failed to retrieve submodel {SubmodelId}: {Message}",
                    submodelId,
                    result.Messages?.ToString() ?? "no message");
                return null;
            }

            var processChain = ExtractProcessChainElement(submodel)
                               ?? WrapSubmodelAsProcessChain(submodel);
            if (processChain != null)
            {
                Context.Set("ProcessChain.FromShell", processChain);
                return processChain;
            }

            if (!string.IsNullOrWhiteSpace(ProcessChainContextKey)
                && Context.Get<object>(ProcessChainContextKey) is SubmodelElementCollection fallback)
            {
                Logger.LogWarning("SendManufacturingRequest: Submodel {SubmodelId} missing ProcessChain element, using context fallback", submodelId);
                return fallback;
            }

            Logger.LogWarning("SendManufacturingRequest: Submodel {SubmodelId} did not contain a ProcessChain element", submodelId);
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendManufacturingRequest: Exception while loading ProcessChain from AAS");
            return null;
        }
    }

    private static SubmodelElement? ExtractProcessChainElement(ISubmodel submodel)
    {
        if (submodel is not Submodel concrete || concrete.SubmodelElements == null)
        {
            return null;
        }

        var elements = concrete.SubmodelElements.Values;
        if (elements == null || !elements.Any())
        {
            return null;
        }

        var match = elements
            .OfType<SubmodelElementCollection>()
            .FirstOrDefault(e => string.Equals(e.IdShort, "ProcessChain", StringComparison.OrdinalIgnoreCase));

        return match;
    }

    private static SubmodelElementCollection? WrapSubmodelAsProcessChain(ISubmodel submodel)
    {
        if (submodel is not Submodel concrete)
        {
            return null;
        }

        if (!string.Equals(concrete.IdShort, "ProcessChain", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var wrapper = new SubmodelElementCollection("ProcessChain");
        if (concrete.SubmodelElements?.Values != null)
        {
            foreach (var element in concrete.SubmodelElements.Values)
            {
                wrapper.Add(element);
            }
        }

        return wrapper;
    }

    private string ResolveSubmodelRepositoryEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(SubmodelRepositoryEndpoint))
        {
            return ResolvePlaceholders(SubmodelRepositoryEndpoint);
        }

        return Context.Get<string>("config.AAS.SubmodelRepositoryEndpoint")
               ?? Context.Get<string>("AAS.SubmodelRepositoryEndpoint")
               ?? string.Empty;
    }
}

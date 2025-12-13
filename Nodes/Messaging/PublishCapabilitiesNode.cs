using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Publishes the full capability containers of a loaded CapabilityDescriptionSubmodel to
/// /{namespace}/{moduleId}/Capabilities.
///
/// The payload is a SubmodelElementCollection "Capabilities" containing the container collections
/// from the CapabilitySet.
/// </summary>
public class PublishCapabilitiesNode : BTNode
{
    public string Namespace { get; set; } = string.Empty;
    public string ModuleId { get; set; } = string.Empty;
    public string SourceKey { get; set; } = "CapabilityDescriptionSubmodel";

    public PublishCapabilitiesNode() : base("PublishCapabilities")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("PublishCapabilities: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = !string.IsNullOrWhiteSpace(Namespace)
            ? ResolvePlaceholders(Namespace)
            : (Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket");

        var moduleId = !string.IsNullOrWhiteSpace(ModuleId)
            ? ResolvePlaceholders(ModuleId)
            : ModuleContextHelper.ResolveModuleId(Context);

        var sourceKey = string.IsNullOrWhiteSpace(SourceKey) ? "CapabilityDescriptionSubmodel" : ResolvePlaceholders(SourceKey);
        var submodel = Context.Get<CapabilityDescriptionSubmodel>(sourceKey);
        if (submodel == null)
        {
            Logger.LogWarning("PublishCapabilities: missing '{SourceKey}' in context; skipping", sourceKey);
            return NodeStatus.Success;
        }

        var containers = submodel.CapabilitySet
            .OfType<SubmodelElementCollection>()
            .ToList();

        var payload = new SubmodelElementCollection("Capabilities");
        foreach (var c in containers)
        {
            payload.Add(c);
        }

        var agentId = Context.Get<string>("config.Agent.AgentId") ?? Context.Get<string>("AgentId") ?? Context.AgentId;
        var role = Context.AgentRole ?? "Agent";

        var msg = new I40MessageBuilder()
            .From(agentId, role)
            .To("Broadcast", "System")
            .WithType("capabilitiesUpdate")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(payload)
            .Build();

        var topic = $"/{ns}/{moduleId}/Capabilities";
        await client.PublishAsync(msg, topic).ConfigureAwait(false);

        Logger.LogInformation("PublishCapabilities: published {Count} containers to {Topic}", containers.Count, topic);
        return NodeStatus.Success;
    }
}

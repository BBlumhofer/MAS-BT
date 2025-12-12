using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Planning.ProcessChain;

public class ParseCapabilityRequestNode : BTNode
{
    public ParseCapabilityRequestNode() : base("ParseCapabilityRequest") { }

    public override Task<NodeStatus> Execute()
    {
        var incoming = Context.Get<I40Message>("LastReceivedMessage");
        if (incoming == null)
        {
            Logger.LogWarning("ParseCapabilityRequest: no incoming message");
            return Task.FromResult(NodeStatus.Failure);
        }

        var capability = ExtractProperty(incoming, "Capability");
        if (string.IsNullOrWhiteSpace(capability))
        {
            Logger.LogWarning("ParseCapabilityRequest: capability missing in CfP message");
            return Task.FromResult(NodeStatus.Failure);
        }

        var requirementId = ExtractProperty(incoming, "RequirementId") ?? Guid.NewGuid().ToString();
        var productId = ExtractProperty(incoming, "ProductId") ?? string.Empty;
        var conversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString();
        var requesterId = incoming.Frame?.Sender?.Identification?.Id ?? "DispatchingAgent";

        var request = new CapabilityRequestContext
        {
            Capability = capability,
            RequirementId = requirementId,
            ConversationId = conversationId,
            RequesterId = requesterId,
            ProductId = productId
        };

        Context.Set("Planning.CapabilityRequest", request);
        Context.Set("ConversationId", conversationId);

        Logger.LogInformation("ParseCapabilityRequest: capability={Capability} requirement={Requirement}", capability, requirementId);
        return Task.FromResult(NodeStatus.Success);
    }

    private static string? ExtractProperty(I40Message message, string idShort)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            if (element is Property prop && string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
            {
                return TryExtractString(prop.Value?.Value);
            }
        }

        return null;
    }

    private static string? TryExtractString(object? value)
    {
        if (value is string literal)
        {
            return literal;
        }

        return value?.ToString();
    }
}

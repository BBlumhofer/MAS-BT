using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.ManufacturingSequence;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Transport;

/// <summary>
/// Stores incoming manufacturing sequence messages so the transport manager can resolve
/// product-specific storage locations when planning transports.
/// </summary>
public class IngestManufacturingSequenceNode : BTNode
{
    public IngestManufacturingSequenceNode() : base("IngestManufacturingSequence") { }

    public override Task<NodeStatus> Execute()
    {
        var message = Context.Get<I40Message>("LastReceivedMessage");
        if (message == null)
        {
            Logger.LogWarning("IngestManufacturingSequence: no message available");
            return Task.FromResult(NodeStatus.Failure);
        }

        var sequence = ExtractSequence(message);
        if (sequence == null)
        {
            Logger.LogWarning("IngestManufacturingSequence: manufacturing sequence payload missing");
            return Task.FromResult(NodeStatus.Failure);
        }

        var productId = message.Frame?.ConversationId ?? message.Frame?.Sender?.Identification?.Id;
        if (string.IsNullOrWhiteSpace(productId))
        {
            Logger.LogWarning("IngestManufacturingSequence: unable to determine product identifier");
            return Task.FromResult(NodeStatus.Failure);
        }

        var index = Context.Get<Dictionary<string, ManufacturingSequence>>("ManufacturingSequence.ByProduct")
                    ?? new Dictionary<string, ManufacturingSequence>(StringComparer.OrdinalIgnoreCase);
        index[productId] = sequence;
        Context.Set("ManufacturingSequence.ByProduct", index);

        Logger.LogInformation("IngestManufacturingSequence: stored sequence for product {ProductId}", productId);
        return Task.FromResult(NodeStatus.Success);
    }

    private static ManufacturingSequence? ExtractSequence(I40Message message)
    {
        if (message.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            if (element is ManufacturingSequence seq)
            {
                return seq;
            }
        }

        return null;
    }
}

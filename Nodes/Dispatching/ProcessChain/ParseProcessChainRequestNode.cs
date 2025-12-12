using System;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using BaSyx.Models.AdminShell;
using AasSharpClient.Models;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class ParseProcessChainRequestNode : BTNode
{
    public ParseProcessChainRequestNode() : base("ParseProcessChainRequest") { }

    public override Task<NodeStatus> Execute()
    {
        var incoming = Context.Get<I40Message>("LastReceivedMessage");
        if (incoming == null)
        {
            Logger.LogWarning("ParseProcessChainRequest: no incoming message available");
            return Task.FromResult(NodeStatus.Failure);
        }

        var ctx = new ProcessChainNegotiationContext
        {
            ConversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString(),
            RequesterId = incoming.Frame?.Sender?.Identification?.Id ?? "Unknown"
        };

        ctx.ProductId = ExtractProductIdentifier(incoming) ?? string.Empty;

        var containers = ExtractCapabilityContainers(incoming).ToList();
        // create requirements from explicit capability containers
        foreach (var container in containers)
        {
            ctx.Requirements.Add(container);
        }


        if (ctx.Requirements.Count == 0)
        {
            Logger.LogWarning("ParseProcessChainRequest: no capabilities found in request");
            return Task.FromResult(NodeStatus.Failure);
        }

        Context.Set("ConversationId", ctx.ConversationId);
        Context.Set("ProcessChain.Negotiation", ctx);
        Logger.LogInformation("ParseProcessChainRequest: parsed {Count} capability requirements", ctx.Requirements.Count);
        return Task.FromResult(NodeStatus.Success);
    }

    private static IEnumerable<CapabilityContainer> ExtractCapabilityContainers(I40Message message)
    {
        if (message == null)
        {
            return Array.Empty<CapabilityContainer>();
        }
        var containers = new List<CapabilityContainer>();
        var temp = new List<SubmodelElementCollection>();
        if (message.InteractionElements != null)
        {
            foreach (var element in message.InteractionElements)
            {
                CollectContainers(element, temp);
            }
        }

        foreach (var smc in temp)
        {
            if (smc != null)
            {
                containers.Add(new CapabilityContainer(smc));
            }
        }

        return containers;
    }

    private static void CollectContainers(ISubmodelElement element, IList<SubmodelElementCollection> sink)
    {
        if (element is SubmodelElementCollection collection)
        {
            if (collection.Values?.OfType<Capability>().Any() == true)
            {
                sink.Add(collection);
                return;
            }

            if (collection.Values != null)
            {
                foreach (var child in collection.Values)
                {
                    CollectContainers(child, sink);
                }
            }
        }
        else if (element is SubmodelElementList list)
        {
            foreach (var child in list)
            {
                CollectContainers(child, sink);
            }
        }
    }

    private static IEnumerable<string> ExtractRequestedCapabilities(I40Message message)
    {
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (message?.InteractionElements == null)
        {
            return capabilities;
        }

        foreach (var element in message.InteractionElements)
        {
            CollectCapabilities(element, capabilities);
        }

        return capabilities;
    }

    private static void CollectCapabilities(ISubmodelElement element, ISet<string> sink)
    {
        switch (element)
        {
            case Property property when IsCapabilityProperty(property.IdShort):
                var parsed = TryExtractString(property.Value?.Value);
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    sink.Add(parsed!);
                }
                break;
            case SubmodelElementCollection collection:
                if (collection.Values != null)
                {
                    TryAddCapabilityFromCollection(collection, sink);
                    foreach (var child in collection.Values)
                    {
                        CollectCapabilities(child, sink);
                    }
                }
                break;
            case SubmodelElementList list:
                foreach (var child in list)
                {
                    CollectCapabilities(child, sink);
                }
                break;
        }
    }

    private static bool TryAddCapabilityFromCollection(SubmodelElementCollection collection, ISet<string> sink)
    {
        if (collection.Values == null)
        {
            return false;
        }

        var capabilityElement = collection.Values.OfType<Capability>().FirstOrDefault();
        string? capabilityName = null;

        if (capabilityElement != null && !string.IsNullOrWhiteSpace(capabilityElement.IdShort))
        {
            capabilityName = capabilityElement.IdShort;
        }
        else if (!string.IsNullOrWhiteSpace(collection.IdShort) &&
                 collection.Values.OfType<Capability>().Any())
        {
            capabilityName = collection.IdShort;
        }

        if (!string.IsNullOrWhiteSpace(capabilityName))
        {
            sink.Add(capabilityName);
            return true;
        }

        return false;
    }

    private static bool IsCapabilityProperty(string? idShort)
    {
        if (string.IsNullOrWhiteSpace(idShort))
        {
            return false;
        }

        return idShort.Equals("Capability", StringComparison.OrdinalIgnoreCase)
               || idShort.Equals("RequiredCapability", StringComparison.OrdinalIgnoreCase)
               || idShort.Equals("CapabilityName", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractProductIdentifier(I40Message message)
    {
        if (message?.InteractionElements != null)
        {
            foreach (var element in message.InteractionElements)
            {
                var match = FindProductIdentifier(element);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }

        return message?.Frame?.Sender?.Identification?.Id;
    }

    private static readonly string[] ProductIdentifierCandidates =
    {
        "ProductId",
        "ProductIdentifier",
        "ProductSerialNumber",
        "SerialNumber"
    };

    private static string? FindProductIdentifier(ISubmodelElement element)
    {
        if (element is Property prop && ProductIdentifierCandidates.Any(candidate =>
                string.Equals(candidate, prop.IdShort, StringComparison.OrdinalIgnoreCase)))
        {
            return TryExtractString(prop.Value?.Value);
        }

        if (element is SubmodelElementCollection collection && collection.Values != null)
        {
            foreach (var child in collection.Values)
            {
                var match = FindProductIdentifier(child);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }
        else if (element is SubmodelElementList list)
        {
            foreach (var child in list)
            {
                var match = FindProductIdentifier(child);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static string? TryExtractString(object? value)
    {
        if (value is string s)
        {
            return s;
        }

        return value?.ToString();
    }

    private static string DetermineCapabilityName(SubmodelElementCollection container)
    {
        if (container == null)
        {
            return "Capability";
        }

        var capabilityElem = container.Values?.OfType<Capability>().FirstOrDefault();
        string? name = null;

        if (capabilityElem != null && !string.IsNullOrWhiteSpace(capabilityElem.IdShort))
        {
            name = capabilityElem.IdShort;
        }
        else if (!string.IsNullOrWhiteSpace(container.IdShort) && container.IdShort!.EndsWith("Container", StringComparison.OrdinalIgnoreCase) && container.Values.OfType<Capability>().Any())
        {
            name = container.IdShort[..^"Container".Length];
        }

        return !string.IsNullOrWhiteSpace(name) ? name! : (container.IdShort ?? "Capability");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using I40Sharp.Messaging.Models;
using Microsoft.Extensions.Logging;
using ProcessChainModel = AasSharpClient.Models.ProcessChain.ProcessChain;
using RequiredCapabilityModel = AasSharpClient.Models.ProcessChain.RequiredCapability;

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

        // Persist the original request so downstream nodes can access raw request details.
        Context.Set("ProcessChain.RequestMessage", incoming);

        var ctx = new ProcessChainNegotiationContext
        {
            ConversationId = incoming.Frame?.ConversationId ?? Guid.NewGuid().ToString(),
            RequesterId = incoming.Frame?.Sender?.Identification?.Id ?? "Unknown"
        };

        ctx.ProductId = ExtractProductIdentifier(incoming) ?? string.Empty;
        var processChainElement = ResolveProcessChainElement(incoming);
        if (processChainElement != null)
        {
            ctx.RequestProcessChainElement = processChainElement;
        }

        var requestedMetadata = ExtractRequestedCapabilityMetadata(processChainElement).ToList();
        var containers = ExtractCapabilityContainers(incoming).ToList();

        if (requestedMetadata.Count > 0 && requestedMetadata.Count != containers.Count)
        {
            Logger.LogWarning(
                "ParseProcessChainRequest: capability metadata count mismatch (containers={ContainerCount}, requestEntries={RequestEntries})",
                containers.Count,
                requestedMetadata.Count);
        }

        for (var i = 0; i < containers.Count; i++)
        {
            var requirement = ctx.Requirements.Add(containers[i]);
            var metadata = ResolveMetadataForRequirement(requirement, requestedMetadata, i);
            ApplyRequestedMetadata(requirement, metadata);
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
                var parsed = property.GetText();
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
            return prop.GetText();
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

        var capabilityValues = container.Values?.OfType<Capability>() ?? Array.Empty<Capability>();
        var capabilityElem = capabilityValues.FirstOrDefault();
        string? name = null;

        if (capabilityElem != null && !string.IsNullOrWhiteSpace(capabilityElem.IdShort))
        {
            name = capabilityElem.IdShort;
        }
        else if (!string.IsNullOrWhiteSpace(container.IdShort)
                 && container.IdShort!.EndsWith("Container", StringComparison.OrdinalIgnoreCase)
                 && capabilityValues.Any())
        {
            name = container.IdShort[..^"Container".Length];
        }

        return !string.IsNullOrWhiteSpace(name) ? name! : (container.IdShort ?? "Capability");
    }

    private SubmodelElementCollection? ResolveProcessChainElement(I40Message message)
    {
        try
        {
            var cached = Context.Get<SubmodelElementCollection>("ProcessChain.Result");
            if (cached != null)
            {
                return cached;
            }
        }
        catch
        {
            // Context lookup may throw if key missing - ignore and scan the message instead.
        }

        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements.OfType<SubmodelElementCollection>())
        {
            if (string.Equals(element.IdShort, "ProcessChain", StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }
        }

        return null;
    }

    private static IReadOnlyList<RequestedCapabilityMetadata> ExtractRequestedCapabilityMetadata(SubmodelElementCollection? processChainElement)
    {
        if (processChainElement?.Values == null)
        {
            return Array.Empty<RequestedCapabilityMetadata>();
        }

        var requiredList = processChainElement.Values
            .OfType<SubmodelElementList>()
            .FirstOrDefault(list => string.Equals(list.IdShort, ProcessChainModel.RequiredCapabilitiesIdShort, StringComparison.OrdinalIgnoreCase));

        if (requiredList == null || requiredList.Count == 0)
        {
            return Array.Empty<RequestedCapabilityMetadata>();
        }

        var metadata = new List<RequestedCapabilityMetadata>(requiredList.Count);
        foreach (var entry in requiredList)
        {
            if (entry is not SubmodelElementCollection collection)
            {
                continue;
            }

            var data = new RequestedCapabilityMetadata(collection)
            {
                InstanceIdentifier = ExtractPropertyString(collection, RequiredCapabilityModel.InstanceIdentifierIdShort),
                CapabilityReference = ExtractReference(collection, RequiredCapabilityModel.RequiredCapabilityReferenceIdShort),
                CapabilityName = ExtractCapabilityName(collection)
            };

            metadata.Add(data);
        }

        return metadata;
    }

    private static RequestedCapabilityMetadata? ResolveMetadataForRequirement(
        CapabilityRequirement requirement,
        IList<RequestedCapabilityMetadata> metadata,
        int index)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return null;
        }

        RequestedCapabilityMetadata? candidate = null;
        if (index < metadata.Count && !metadata[index].Assigned)
        {
            candidate = metadata[index];
        }
        else if (!string.IsNullOrWhiteSpace(requirement.Capability))
        {
            candidate = metadata.FirstOrDefault(m =>
                !m.Assigned
                && !string.IsNullOrWhiteSpace(m.CapabilityName)
                && string.Equals(m.CapabilityName, requirement.Capability, StringComparison.OrdinalIgnoreCase));
        }

        candidate ??= metadata.FirstOrDefault(m => !m.Assigned);

        if (candidate != null)
        {
            candidate.Assigned = true;
        }

        return candidate;
    }

    private static void ApplyRequestedMetadata(CapabilityRequirement requirement, RequestedCapabilityMetadata? metadata)
    {
        if (requirement == null || metadata == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(metadata.InstanceIdentifier))
        {
            requirement.RequirementId = metadata.InstanceIdentifier!;
            requirement.RequestedInstanceIdentifier = metadata.InstanceIdentifier;
        }

        if (metadata.CapabilityReference != null)
        {
            requirement.RequestedCapabilityReference = metadata.CapabilityReference;
        }

        requirement.RequestedCapabilityElement = metadata.Element;

        if (string.IsNullOrWhiteSpace(requirement.Capability) && !string.IsNullOrWhiteSpace(metadata.CapabilityName))
        {
            requirement.Capability = metadata.CapabilityName!;
        }
    }

    private static string? ExtractPropertyString(SubmodelElementCollection collection, string idShort)
    {
        if (collection?.Values == null)
        {
            return null;
        }

        var property = collection.Values
            .OfType<Property>()
            .FirstOrDefault(prop => string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase));

        var extracted = property?.GetText();
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
    }

    private static Reference? ExtractReference(SubmodelElementCollection collection, string idShort)
    {
        if (collection?.Values == null)
        {
            return null;
        }

        var referenceElement = collection.Values
            .OfType<ReferenceElement>()
            .FirstOrDefault(r => string.Equals(r.IdShort, idShort, StringComparison.OrdinalIgnoreCase));

        var reference = AasValueUnwrap.Unwrap(referenceElement?.Value) as IReference;
        return CloneReference(reference);
    }

    private static Reference? CloneReference(IReference? reference)
    {
        if (reference == null)
        {
            return null;
        }

        var keyList = reference.Keys?
            .Select(k => (IKey)new Key(k.Type, k.Value))
            .ToList();
        if (keyList == null || keyList.Count == 0)
        {
            return null;
        }

        return new Reference(keyList)
        {
            Type = reference.Type
        };
    }

    private static string? ExtractCapabilityName(SubmodelElementCollection collection)
    {
        if (collection?.Values == null)
        {
            return null;
        }

        string? TryExtract(SubmodelElementCollection source)
        {
            var capability = source.Values?.OfType<Capability>().FirstOrDefault();
            return string.IsNullOrWhiteSpace(capability?.IdShort) ? null : capability.IdShort;
        }

        var direct = TryExtract(collection);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var child in collection.Values.OfType<SubmodelElementCollection>())
        {
            var nested = TryExtract(child);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private sealed class RequestedCapabilityMetadata
    {
        public RequestedCapabilityMetadata(SubmodelElementCollection element)
        {
            Element = element;
        }

        public SubmodelElementCollection Element { get; }
        public string? InstanceIdentifier { get; set; }
        public Reference? CapabilityReference { get; set; }
        public string? CapabilityName { get; set; }
        public bool Assigned { get; set; }
    }
}

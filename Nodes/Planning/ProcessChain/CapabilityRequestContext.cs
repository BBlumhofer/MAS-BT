using System;
using System.Collections.Generic;
using System.Linq;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Planning.ProcessChain;

/// <summary>
/// Holds parsed data from a capability call-for-proposal.
/// </summary>
public class CapabilityRequestContext
{
    public string ConversationId { get; set; } = string.Empty;
    public string RequirementId { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string RequesterId { get; set; } = string.Empty;
    public string RequesterRole { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public CapabilityContainer? CapabilityContainer { get; set; }
    public string FrameType { get; set; } = string.Empty;
    public string BaseMessageType { get; set; } = string.Empty;
    public string MessageSubtype { get; set; } = string.Empty;
    public I40MessageTypeSubtypes SubType { get; set; } = I40MessageTypeSubtypes.None;
    public bool IsManufacturingRequest => SubType == I40MessageTypeSubtypes.ManufacturingSequence;
    public SubmodelElementCollection? RequestedCapabilityElement { get; set; }
    public SubmodelElementCollection? AssetLocation { get; set; }
    public string? RequestedInstanceIdentifier { get; set; }
    public Reference? RequestedCapabilityReference { get; set; }

    private static readonly string[] CapabilityPropertyCandidates =
    {
        "Capability",
        "RequiredCapability",
        "CapabilityName"
    };

    public static CapabilityRequestContext FromMessage(I40Message message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var capability = ExtractCapabilityName(message);
        var requirementId = ExtractProperty(message, "RequirementId") ?? Guid.NewGuid().ToString();
        var productId = ExtractProperty(message, "ProductId") ?? string.Empty;
        var conversationId = message.Frame?.ConversationId ?? Guid.NewGuid().ToString();
        var requesterId = message.Frame?.Sender?.Identification?.Id ?? "DispatchingAgent";
        var requesterRole = message.Frame?.Sender?.Role?.Name ?? string.Empty;
        var frameTypeRaw = message.Frame?.Type ?? string.Empty;
        var (baseType, subtypeToken) = SplitMessageType(frameTypeRaw);
        if (!I40MessageTypeSubtypesExtensions.TryParse(subtypeToken, out var typedSubtype))
        {
            typedSubtype = I40MessageTypeSubtypes.None;
        }

        var context = new CapabilityRequestContext
        {
            Capability = capability ?? string.Empty,
            RequirementId = requirementId,
            ConversationId = conversationId,
            RequesterId = requesterId,
            RequesterRole = requesterRole ?? string.Empty,
            ProductId = productId,
            FrameType = frameTypeRaw,
            BaseMessageType = baseType,
            MessageSubtype = subtypeToken,
            SubType = typedSubtype
        };

        var containerElement = FindCapabilityContainer(message, capability);
        if (containerElement != null)
        {
            context.RequestedCapabilityElement = containerElement;
            context.CapabilityContainer = new CapabilityContainer(containerElement);
            context.RequestedInstanceIdentifier = ExtractInstanceIdentifier(containerElement);
            context.RequestedCapabilityReference = ExtractCapabilityReference(containerElement);
        }

        // Extract AssetLocation element if present
        if (message.InteractionElements != null)
        {
            var asset = message.InteractionElements.OfType<SubmodelElementCollection>()
                .FirstOrDefault(e => string.Equals(e.IdShort, "AssetLocation", StringComparison.OrdinalIgnoreCase));
            if (asset != null)
            {
                context.AssetLocation = asset;
            }
        }

        return context;
    }

    private static (string BaseType, string SubType) SplitMessageType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, string.Empty);
        }

        var parts = raw.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (parts.Length == 1)
        {
            return (parts[0], string.Empty);
        }

        return (parts[0], parts[1]);
    }

    private static string? ExtractCapabilityName(I40Message message)
    {
        foreach (var candidate in CapabilityPropertyCandidates)
        {
            var value = ExtractProperty(message, candidate);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
                return prop.GetText();
            }
        }

        return null;
    }

    private static SubmodelElementCollection? FindCapabilityContainer(I40Message message, string? capabilityName)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(capabilityName))
        {
            var match = FindCapabilityContainer(message.InteractionElements, capabilityName, requireMatch: true);
            if (match != null)
            {
                return match;
            }
        }

        return FindCapabilityContainer(message.InteractionElements, capabilityName: null, requireMatch: false);
    }

    private static SubmodelElementCollection? FindCapabilityContainer(IEnumerable<ISubmodelElement>? elements, string? capabilityName, bool requireMatch)
    {
        if (elements == null)
        {
            return null;
        }

        foreach (var element in elements)
        {
            if (element == null)
            {
                continue;
            }

            var match = FindCapabilityContainer(element, capabilityName, requireMatch);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static SubmodelElementCollection? FindCapabilityContainer(ISubmodelElement element, string? capabilityName, bool requireMatch)
    {
        switch (element)
        {
            case SubmodelElementCollection collection:
            {
                var containsCapabilityElements = collection.Values?.OfType<Capability>().Any() == true;
                if (containsCapabilityElements)
                {
                    if (!requireMatch || MatchesCapability(collection, capabilityName))
                    {
                        return collection;
                    }
                }

                if (collection.Values != null)
                {
                    var nested = FindCapabilityContainer(collection.Values, capabilityName, requireMatch);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
                break;
            }
            case SubmodelElementList list:
                foreach (var child in list)
                {
                    var nested = FindCapabilityContainer(child, capabilityName, requireMatch);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
                break;
        }

        return null;
    }

    private static bool MatchesCapability(SubmodelElementCollection collection, string? capabilityName)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return true;
        }

        if (collection.Values?.OfType<Capability>().Any(cap =>
                string.Equals(cap.IdShort, capabilityName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(collection.IdShort))
        {
            if (string.Equals(collection.IdShort, capabilityName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            const string suffix = "Container";
            if (collection.IdShort.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = collection.IdShort[..^suffix.Length];
                if (string.Equals(prefix, capabilityName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ExtractInstanceIdentifier(SubmodelElementCollection collection)
    {
        if (collection?.Values == null)
        {
            return null;
        }

        var property = collection.Values
            .OfType<Property>()
            .FirstOrDefault(prop => string.Equals(prop.IdShort, RequiredCapability.InstanceIdentifierIdShort, StringComparison.OrdinalIgnoreCase));

        return property?.GetText();
    }

    private static Reference? ExtractCapabilityReference(SubmodelElementCollection collection)
    {
        if (collection?.Values == null)
        {
            return null;
        }

        var referenceElement = collection.Values
            .OfType<ReferenceElement>()
            .FirstOrDefault(r => string.Equals(r.IdShort, RequiredCapability.RequiredCapabilityReferenceIdShort, StringComparison.OrdinalIgnoreCase));

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
}

/// <summary>
/// Planned response metadata that will be serialized into the proposal message.
/// </summary>
public class CapabilityOfferPlan
{
    public string OfferId { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan SetupTime { get; set; } = TimeSpan.Zero;
    public TimeSpan CycleTime { get; set; } = TimeSpan.FromMinutes(1);
    public double Cost { get; set; } = 0.0;
    public OfferedCapability? OfferedCapability { get; set; }
    public List<OfferedCapability> SupplementalCapabilities { get; } = new List<OfferedCapability>();
}

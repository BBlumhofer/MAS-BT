using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ProcessChain;
using AasSharpClient.Models.ManufacturingSequence;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Services.Graph;
using Microsoft.Extensions.Logging;
using ProcessChainModel = AasSharpClient.Models.ProcessChain.ProcessChain;
using RequiredCapabilityModel = AasSharpClient.Models.ProcessChain.RequiredCapability;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class BuildProcessChainResponseNode : BTNode
{
    public BuildProcessChainResponseNode() : base("BuildProcessChainResponse") { }

    public override Task<NodeStatus> Execute()
    {
        var negotiation = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (negotiation == null)
        {
            Logger.LogError("BuildProcessChainResponse: negotiation context missing");
            return Task.FromResult(NodeStatus.Failure);
        }
        Logger.LogInformation("Valid Offers received start building ProcessChain Offer Response");
        var requestType = Context.Get<string>("ProcessChain.RequestType");
        var isManufacturing = string.Equals(requestType, "ManufacturingSequence", StringComparison.OrdinalIgnoreCase);

        SubmodelElement resultElement = isManufacturing
            ? BuildManufacturingSequenceModel(negotiation)
            : BuildProcessChainModel(negotiation);

        var success = negotiation.HasCompleteProcessChain;
        Context.Set("ProcessChain.Result", resultElement);
        Context.Set("ProcessChain.Success", success);
        if (isManufacturing)
        {
            Context.Set("ManufacturingSequence.Result", resultElement);
            Context.Set("ManufacturingSequence.Success", success);
        }

        if (!success)
        {
            var bestMatchByRequirementId = Context.Get<Dictionary<string, string>>("ProcessChain.SimilarityBestMatch")
                                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var req in negotiation.Requirements)
            {
                if (req.CapabilityOffers.Count > 0)
                {
                    continue;
                }

                bestMatchByRequirementId.TryGetValue(req.RequirementId, out var best);
                Logger.LogWarning(
                    "BuildProcessChainResponse: requirement failed (no offers). Capability={Capability} RequirementId={RequirementId} Similarity={Best}",
                    req.Capability,
                    req.RequirementId,
                    string.IsNullOrWhiteSpace(best) ? "<unknown>" : best);
            }
        }

        Logger.LogInformation(
            "BuildProcessChainResponse: built {Mode} with {Count} requirements (success={Success})",
            isManufacturing ? "ManufacturingSequence" : "ProcessChain",
            negotiation.Requirements.Count,
            success);
        return Task.FromResult(NodeStatus.Success);
    }

    private ProcessChainModel BuildProcessChainModel(ProcessChainNegotiationContext negotiation)
    {
        var processChain = new ProcessChainModel();
        var requirementIndex = 0;
        foreach (var requirement in negotiation.Requirements)
        {
            var requiredCapability = new RequiredCapabilityModel($"RequiredCapability_{++requirementIndex}");
            requiredCapability.SetInstanceIdentifier(requirement.RequirementId);
            requiredCapability.SetRequiredCapabilityReference(CreateCapabilityReference(requirement, negotiation));

            foreach (var offer in requirement.CapabilityOffers)
            {
                requiredCapability.AddOfferedCapability(offer);
            }

            processChain.AddRequiredCapability(requiredCapability);
        }

        return processChain;
    }

    private ManufacturingSequence BuildManufacturingSequenceModel(ProcessChainNegotiationContext negotiation)
    {
        var sequence = new ManufacturingSequence();
        var requirementIndex = 0;
        foreach (var requirement in negotiation.Requirements)
        {
            var requiredCapability = new ManufacturingRequiredCapability($"RequiredCapability_{++requirementIndex}");
            requiredCapability.SetInstanceIdentifier(requirement.RequirementId);
            requiredCapability.SetRequiredCapabilityReference(CreateCapabilityReference(requirement, negotiation));

            foreach (var offer in requirement.CapabilityOffers)
            {
                var offeredSequence = new ManufacturingOfferedCapabilitySequence();
                AppendCapabilitySequence(offeredSequence, offer);
                requiredCapability.AddSequence(offeredSequence);
            }

            sequence.AddRequiredCapability(requiredCapability);
        }

        return sequence;
    }

    private void AppendCapabilitySequence(ManufacturingOfferedCapabilitySequence targetSequence, OfferedCapability offeredCapability)
    {
        if (targetSequence == null || offeredCapability == null)
        {
            return;
        }

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void TryAdd(OfferedCapability cap)
        {
            if (cap == null)
            {
                return;
            }

            var id = cap.InstanceIdentifier.GetText() ?? string.Empty;
            if (!added.Add(id))
            {
                return;
            }

            targetSequence.AddCapability(cap);
        }

        var pre = new List<OfferedCapability>();
        var post = new List<OfferedCapability>();
        var nestedCapabilities = new List<OfferedCapability>();
        foreach (var element in offeredCapability.CapabilitySequence)
        {
            if (element is OfferedCapability nested)
            {
                nestedCapabilities.Add(nested);
                if (IsPostPlacement(nested))
                {
                    post.Add(nested);
                }
                else
                {
                    pre.Add(nested);
                }
            }
        }
        offeredCapability.CapabilitySequence.Clear();
        foreach (var nested in nestedCapabilities)
        {
            StripCapabilitySequence(nested);
        }
        RemoveCapabilitySequenceContainer(offeredCapability);

        foreach (var nested in pre)
        {
            TryAdd(nested);
        }

        TryAdd(offeredCapability);

        foreach (var nested in post)
        {
            TryAdd(nested);
        }
    }

    private static void StripCapabilitySequence(OfferedCapability capability)
    {
        if (capability == null)
        {
            return;
        }

        if (capability.CapabilitySequence.Count > 0)
        {
            foreach (var child in capability.CapabilitySequence.OfType<OfferedCapability>())
            {
                StripCapabilitySequence(child);
            }
        }

        capability.CapabilitySequence.Clear();
        RemoveCapabilitySequenceContainer(capability);
    }

    private static void RemoveCapabilitySequenceContainer(OfferedCapability capability)
    {
        if (capability == null)
        {
            return;
        }

        capability.CapabilitySequence.Clear();
        capability.Remove(OfferedCapability.CapabilitySequenceIdShort);
    }

    private static bool IsPostPlacement(OfferedCapability capability)
    {
        var placement = capability?.SequencePlacement?.GetText();
        if (string.IsNullOrWhiteSpace(placement))
        {
            return false;
        }

        return placement.Equals("post", StringComparison.OrdinalIgnoreCase)
               || placement.Equals("after", StringComparison.OrdinalIgnoreCase)
               || placement.Equals("afterCapability", StringComparison.OrdinalIgnoreCase);
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

    private Reference CreateCapabilityReference(CapabilityRequirement requirement, ProcessChainNegotiationContext negotiation)
    {
        if (requirement?.RequestedCapabilityReference != null)
        {
            var cached = CloneReference(requirement.RequestedCapabilityReference);
            if (cached != null)
            {
                return cached;
            }
        }

        var capability = requirement?.Capability ?? string.Empty;
        var conversationId = negotiation?.ConversationId ?? "<unknown>";

        if (string.IsNullOrWhiteSpace(capability))
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: capability idShort missing for requirement in conversation {negotiation?.ConversationId}");
        }

        // Query must use the sender (RequesterId) of the ProcessChain request (product's capability submodel lives on the requester).
        var senderShellId = negotiation?.RequesterId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(senderShellId))
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: sender shell id missing in negotiation context (conversation={negotiation?.ConversationId})");
        }

        var referenceQuery = Context.Get<ICapabilityReferenceQuery>("CapabilityReferenceQuery");
        if (referenceQuery == null)
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: capability reference query service missing (conversation={conversationId})");
        }

        string? json = null;
        try
        {
            json = referenceQuery.GetCapabilityReferenceJsonAsync(senderShellId, capability).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: failed to query Neo4j for capability reference (conversation={conversationId} sender={senderShellId} capability={capability}): {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(json) || !TryParseNeo4jReferenceJson(json, out var modelRef))
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: could not resolve capability reference from graph (conversation={conversationId} sender={senderShellId} capability={capability}) RawNeo4jResponse={json ?? "<null>"}");
        }

        return modelRef;
    }

    private sealed record Neo4jReferenceKey(string? Type, string? Value);

    private static bool TryParseNeo4jReferenceJson(string json, out Reference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        List<Neo4jReferenceKey>? keysDto;
        try
        {
            keysDto = JsonSerializer.Deserialize<List<Neo4jReferenceKey>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return false;
        }

        if (keysDto == null || keysDto.Count == 0)
        {
            return false;
        }

        var keys = new List<IKey>();
        foreach (var dto in keysDto)
        {
            var typeText = dto?.Type;
            var valueText = dto?.Value;
            if (string.IsNullOrWhiteSpace(typeText) || string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            if (!TryMapKeyType(typeText!, out var keyType))
            {
                continue;
            }

            keys.Add(new Key(keyType, valueText!.Trim()));
        }

        if (keys.Count == 0)
        {
            return false;
        }

        reference = new Reference(keys)
        {
            Type = ReferenceType.ModelReference
        };
        return true;
    }

    private static bool TryMapKeyType(string typeText, out KeyType keyType)
    {
        keyType = default;

        if (string.IsNullOrWhiteSpace(typeText))
        {
            return false;
        }

        if (Enum.TryParse<KeyType>(typeText.Trim(), ignoreCase: true, out var parsed))
        {
            keyType = parsed;
            return true;
        }

        // tolerate common variations
        if (string.Equals(typeText, "SubmodelElementCollection", StringComparison.OrdinalIgnoreCase))
        {
            keyType = KeyType.SubmodelElementCollection;
            return true;
        }

        return false;
    }
}

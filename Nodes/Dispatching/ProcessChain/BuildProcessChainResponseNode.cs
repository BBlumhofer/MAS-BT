using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AasSharpClient.Models.ProcessChain;
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

        var success = negotiation.HasCompleteProcessChain;
        Context.Set("ProcessChain.Result", processChain);
        Context.Set("ProcessChain.Success", success);

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

        Logger.LogInformation("BuildProcessChainResponse: built process chain with {Count} requirements (success={Success})", requirementIndex, success);
        return Task.FromResult(NodeStatus.Success);
    }

    private Reference CreateCapabilityReference(CapabilityRequirement requirement, ProcessChainNegotiationContext negotiation)
    {
        var capability = requirement?.Capability ?? string.Empty;

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
            throw new InvalidOperationException($"BuildProcessChainResponse: capability reference query service missing (conversation={negotiation.ConversationId})");
        }

        string? json = null;
        try
        {
            json = referenceQuery.GetCapabilityReferenceJsonAsync(senderShellId, capability).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: failed to query Neo4j for capability reference (conversation={negotiation.ConversationId} sender={senderShellId} capability={capability}): {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(json) || !TryParseNeo4jReferenceJson(json, out var modelRef))
        {
            throw new InvalidOperationException($"BuildProcessChainResponse: could not resolve capability reference from graph (conversation={negotiation.ConversationId} sender={senderShellId} capability={capability}) RawNeo4jResponse={json ?? "<null>"}");
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

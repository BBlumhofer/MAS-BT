using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaSyx.Clients.AdminShell.Http;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Loads an existing ProcessChain submodel from the agent's AAS, extracts the ProcessChain element,
/// and stores it on the blackboard for reuse.
/// </summary>
public class LoadProcessChainFromShellNode : BTNode
{
    public string ContextKey { get; set; } = "ProcessChain.Result";
    public string SubmodelIdContextKey { get; set; } = "ProcessChain.SubmodelId";
    public string SubmodelRepositoryEndpoint { get; set; } = string.Empty;
    public string TargetElementIdShort { get; set; } = "ProcessChain";

    public LoadProcessChainFromShellNode() : base("LoadProcessChainFromShell")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var endpoint = ResolveRepositoryEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Logger.LogWarning("LoadProcessChainFromShell: SubmodelRepositoryEndpoint missing");
            return NodeStatus.Failure;
        }

        var identifiers = CollectCandidateIdentifiers();
        if (identifiers.Count == 0)
        {
            Logger.LogInformation("LoadProcessChainFromShell: No ProcessChain submodel references available");
            return NodeStatus.Failure;
        }

        try
        {
            using var client = new SubmodelRepositoryHttpClient(new Uri(endpoint));
            foreach (var id in identifiers)
            {
                try
                {
                    var result = await client.RetrieveSubmodelAsync(new Identifier(id)).ConfigureAwait(false);
                    if (!result.Success || result.Entity is not ISubmodel submodel)
                    {
                        Logger.LogDebug("LoadProcessChainFromShell: Unable to retrieve submodel {Id} (success={Success})", id, result.Success);
                        continue;
                    }

                    var element = ExtractProcessChainElement(submodel)
                                  ?? WrapSubmodelAsProcessChain(submodel);
                    if (element == null)
                    {
                        Logger.LogDebug("LoadProcessChainFromShell: Submodel {Id} does not contain target element {Element}", id, TargetElementIdShort);
                        continue;
                    }

                    Context.Set(ContextKey, element);
                    Context.Set(SubmodelIdContextKey, submodel.Id?.Id ?? id);
                    Context.Set("ProcessChain.Submodel", submodel);
                    Logger.LogInformation("LoadProcessChainFromShell: Loaded ProcessChain from submodel {Id}", submodel.Id?.Id ?? id);
                    return NodeStatus.Success;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "LoadProcessChainFromShell: Failed to retrieve ProcessChain submodel {Id}", id);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "LoadProcessChainFromShell: Error connecting to repository {Endpoint}", endpoint);
            return NodeStatus.Failure;
        }

        Logger.LogInformation("LoadProcessChainFromShell: No ProcessChain element found across {Count} references", identifiers.Count);
        return NodeStatus.Failure;
    }

    private string ResolveRepositoryEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(SubmodelRepositoryEndpoint))
        {
            return ResolvePlaceholders(SubmodelRepositoryEndpoint);
        }

        return Context.Get<string>("config.AAS.SubmodelRepositoryEndpoint")
               ?? Context.Get<string>("AAS.SubmodelRepositoryEndpoint")
               ?? string.Empty;
    }

    private List<string> CollectCandidateIdentifiers()
    {
        var candidates = new List<string>();
        var explicitId = Context.Get<string>(SubmodelIdContextKey);
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            candidates.Add(explicitId);
        }

        var fromUpload = Context.Get<string>("Submodel.LastUploadedId");
        if (!string.IsNullOrWhiteSpace(fromUpload))
        {
            candidates.Add(fromUpload);
        }

        var refs = Context.Get<List<IReference>>("AAS.Shell.SubmodelReferences")
                   ?? Context.Get<IAssetAdministrationShell>("AAS.Shell")?.SubmodelReferences?.Cast<IReference>().ToList()
                   ?? new List<IReference>();

        foreach (var reference in refs)
        {
            if (TryGetSubmodelIdentifier(reference, out var identifier))
            {
                candidates.Add(identifier.Id);
            }
        }

        return candidates
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private SubmodelElement? ExtractProcessChainElement(ISubmodel submodel)
    {
        if (submodel is not Submodel concrete || concrete.SubmodelElements == null || concrete.SubmodelElements.Count == 0)
        {
            return null;
        }

        var elements = concrete.SubmodelElements.Values;
        var match = elements
            .OfType<SubmodelElementCollection>()
            .FirstOrDefault(e => string.Equals(e.IdShort, TargetElementIdShort, StringComparison.OrdinalIgnoreCase));

        return match;
    }

    private SubmodelElementCollection? WrapSubmodelAsProcessChain(ISubmodel submodel)
    {
        if (submodel is not Submodel concrete)
        {
            return null;
        }

        if (!string.Equals(concrete.IdShort, TargetElementIdShort, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var wrapper = new SubmodelElementCollection(TargetElementIdShort);
        if (concrete.SubmodelElements?.Values != null)
        {
            foreach (var element in concrete.SubmodelElements.Values)
            {
                wrapper.Add(element);
            }
        }

        return wrapper;
    }

    private static bool TryGetSubmodelIdentifier(IReference reference, out Identifier identifier)
    {
        identifier = null!;
        var keys = reference?.Keys?.ToList();
        if (keys == null || keys.Count == 0)
        {
            return false;
        }

        var key = keys.LastOrDefault(k => k.Type == KeyType.Submodel)
                  ?? keys.LastOrDefault();

        if (key == null || string.IsNullOrWhiteSpace(key.Value))
        {
            return false;
        }

        identifier = new Identifier(key.Value);
        return true;
    }
}

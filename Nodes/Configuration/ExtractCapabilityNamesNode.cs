using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// ExtractCapabilityNames - extracts capability names from a loaded CapabilityDescriptionSubmodel
/// and stores them as a string list in the blackboard.
/// </summary>
public class ExtractCapabilityNamesNode : BTNode
{
    public string SourceKey { get; set; } = "CapabilityDescriptionSubmodel";
    public string TargetKey { get; set; } = "Capabilities";
    public bool FailOnMissing { get; set; } = false;

    public ExtractCapabilityNamesNode() : base("ExtractCapabilityNames")
    {
    }

    public override Task<NodeStatus> Execute()
    {
        var sourceKey = string.IsNullOrWhiteSpace(SourceKey) ? "CapabilityDescriptionSubmodel" : ResolvePlaceholders(SourceKey);
        var targetKey = string.IsNullOrWhiteSpace(TargetKey) ? "Capabilities" : ResolvePlaceholders(TargetKey);

        var submodel = Context.Get<CapabilityDescriptionSubmodel>(sourceKey);
        if (submodel == null)
        {
            if (FailOnMissing)
            {
                Logger.LogError("ExtractCapabilityNames: CapabilityDescriptionSubmodel not loaded from '{SourceKey}'. " +
                              "Possible causes: (1) Shell not found, (2) Submodel missing in repository, or (3) LoadCapabilityDescriptionSubmodel failed.",
                              sourceKey);
                
                // Check if Shell was loaded at all
                var shell = Context.Get<BaSyx.Models.AdminShell.IAssetAdministrationShell>("AAS.Shell");
                if (shell == null)
                {
                    Logger.LogError("  → Root cause: AAS Shell not present in context. Check ReadShell node configuration and AAS repository availability.");
                }
                else
                {
                    Logger.LogWarning("  → Shell is loaded (Id: {ShellId}), but CapabilityDescriptionSubmodel could not be retrieved. Check submodel references.",
                                    shell.Id?.Id ?? "<unknown>");
                }
                
                return Task.FromResult(NodeStatus.Failure);
            }

            Logger.LogWarning("ExtractCapabilityNames: missing '{SourceKey}' in context; keeping {TargetKey} empty", sourceKey, targetKey);
            Context.Set(targetKey, new List<string>());
            Context.Set("CapabilityNames", new List<string>());
            return Task.FromResult(NodeStatus.Success);
        }

        var names = submodel
            .GetCapabilityNames()
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Context.Set(targetKey, names);
        // Also set the alternative key that RegisterAgentNode supports.
        Context.Set("CapabilityNames", names);

        if (names.Count == 0)
        {
            Logger.LogWarning("ExtractCapabilityNames: Loaded CapabilityDescriptionSubmodel, but extracted ZERO capabilities. " +
                            "Submodel IdShort: '{IdShort}', Identifier: '{Id}'. " +
                            "Check if the AAS submodel contains valid CapabilityContainer elements.",
                            submodel.IdShort, submodel.Id?.Id ?? "<no-id>");
        }
        else
        {
            Logger.LogInformation("ExtractCapabilityNames: extracted {Count} capability names from '{SourceKey}': [{Caps}]",
                                names.Count, sourceKey, string.Join(", ", names));
        }
        
        return Task.FromResult(NodeStatus.Success);
    }
}

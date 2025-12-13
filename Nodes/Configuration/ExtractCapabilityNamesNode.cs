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
                Logger.LogError("ExtractCapabilityNames: missing '{SourceKey}' in context", sourceKey);
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

        Logger.LogInformation("ExtractCapabilityNames: extracted {Count} capability names", names.Count);
        return Task.FromResult(NodeStatus.Success);
    }
}

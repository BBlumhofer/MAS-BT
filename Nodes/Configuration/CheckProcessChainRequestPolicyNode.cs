using System;
using System.Text.Json;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Checks whether the agent configuration forces a new ProcessChain request.
/// Returns Success when reusing an existing ProcessChain is allowed, otherwise Failure.
/// </summary>
public class CheckProcessChainRequestPolicyNode : BTNode
{
    /// <summary>
    /// JSON path to the flag within config (e.g. Agent.ForceProcessChainRequest).
    /// </summary>
    public string ConfigFlagPath { get; set; } = "Agent.ForceProcessChainRequest";

    /// <summary>
    /// Default value to apply when the config does not contain the flag.
    /// </summary>
    public bool DefaultForceRequest { get; set; }
        = false;

    public CheckProcessChainRequestPolicyNode() : base("CheckProcessChainRequestPolicy")
    {
    }

    public override Task<NodeStatus> Execute()
    {
        if (string.IsNullOrWhiteSpace(ConfigFlagPath))
        {
            Logger.LogDebug("CheckProcessChainRequestPolicy: No config path provided, allowing existing ProcessChain usage");
            return Task.FromResult(NodeStatus.Success);
        }

        var forceRequest = ResolveFlag();

        if (forceRequest)
        {
            Logger.LogInformation("CheckProcessChainRequestPolicy: ForceProcessChainRequest flag enabled at path {Path}", ConfigFlagPath);
            return Task.FromResult(NodeStatus.Failure);
        }

        Logger.LogInformation("CheckProcessChainRequestPolicy: Existing ProcessChain may be reused (flag {Path} disabled)", ConfigFlagPath);
        return Task.FromResult(NodeStatus.Success);
    }

    private bool ResolveFlag()
    {
        try
        {
            var root = Context.Get<JsonElement>("config");
            if (root.ValueKind == JsonValueKind.Undefined || root.ValueKind == JsonValueKind.Null)
            {
                return DefaultForceRequest;
            }

            var segments = ConfigFlagPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return DefaultForceRequest;
            }

            if (segments.Length > 0 && string.Equals(segments[0], "config", StringComparison.OrdinalIgnoreCase))
            {
                segments = segments[1..];
            }

            if (segments.Length == 0)
            {
                return DefaultForceRequest;
            }

            var current = root;
            foreach (var segment in segments)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                {
                    return DefaultForceRequest;
                }
                current = next;
            }

            return current.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(current.GetString(), out var parsed) => parsed,
                JsonValueKind.Number when current.TryGetInt32(out var number) => number != 0,
                _ => DefaultForceRequest
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CheckProcessChainRequestPolicy: Failed to evaluate config flag {Path}", ConfigFlagPath);
            return DefaultForceRequest;
        }
    }
}

using System;
using System.Text.Json;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Checks a boolean flag inside the agent configuration and succeeds only if it matches the expected value.
/// </summary>
public class CheckConfigFlagNode : BTNode
{
    public string ConfigPath { get; set; } = string.Empty;
    public bool ExpectedValue { get; set; } = true;
    public bool DefaultValue { get; set; } = false;

    public CheckConfigFlagNode() : base("CheckConfigFlag")
    {
    }

    public override Task<NodeStatus> Execute()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            Logger.LogWarning("CheckConfigFlag: ConfigPath missing");
            return Task.FromResult(NodeStatus.Failure);
        }

        var actual = ResolveFlag();
        if (actual == ExpectedValue)
        {
            Logger.LogInformation("CheckConfigFlag: {Path} matched expected value {Value}", ConfigPath, ExpectedValue);
            return Task.FromResult(NodeStatus.Success);
        }

        Logger.LogDebug("CheckConfigFlag: {Path}={Actual} did not match expected {Expected}", ConfigPath, actual, ExpectedValue);
        return Task.FromResult(NodeStatus.Failure);
    }

    private bool ResolveFlag()
    {
        try
        {
            var root = Context.Get<JsonElement>("config");
            if (root.ValueKind == JsonValueKind.Undefined || root.ValueKind == JsonValueKind.Null)
            {
                return DefaultValue;
            }

            var segments = ConfigPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0 && string.Equals(segments[0], "config", StringComparison.OrdinalIgnoreCase))
            {
                segments = segments[1..];
            }

            if (segments.Length == 0)
            {
                return DefaultValue;
            }

            var current = root;
            foreach (var segment in segments)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                {
                    return DefaultValue;
                }
                current = next;
            }

            return current.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(current.GetString(), out var parsed) => parsed,
                JsonValueKind.Number when current.TryGetInt32(out var number) => number != 0,
                _ => DefaultValue
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CheckConfigFlag: Failed to read flag at {Path}", ConfigPath);
            return DefaultValue;
        }
    }
}

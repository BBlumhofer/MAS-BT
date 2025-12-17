using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Tools;
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
            var root = Context.Get<object>("config");
            if (root is null)
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

            return JsonFacade.TryGetPathAsBool(root, segments, out var flag) ? flag : DefaultValue;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CheckConfigFlag: Failed to read flag at {Path}", ConfigPath);
            return DefaultValue;
        }
    }
}

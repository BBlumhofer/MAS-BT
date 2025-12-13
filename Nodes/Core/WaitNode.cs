using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Core;

/// <summary>
/// Wait - Wartet eine bestimmte Zeit
/// Einfache Utility-Node für Delays
/// </summary>
public class WaitNode : BTNode
{
    public int DelayMs { get; set; } = 1000;

    private DateTime? _startedAtUtc;

    public WaitNode() : base("Wait")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        // Tick-based wait: do not block the whole tree execution.
        // First tick starts the timer, subsequent ticks return Running until elapsed.
        if (_startedAtUtc == null)
        {
            _startedAtUtc = DateTime.UtcNow;
            Logger.LogDebug("Wait: Started waiting {DelayMs}ms", DelayMs);
            return NodeStatus.Running;
        }

        var elapsedMs = (DateTime.UtcNow - _startedAtUtc.Value).TotalMilliseconds;
        if (elapsedMs >= DelayMs)
        {
            _startedAtUtc = null;
            return NodeStatus.Success;
        }

        return NodeStatus.Running;
    }

    public override Task OnAbort()
    {
        _startedAtUtc = null;
        return Task.CompletedTask;
    }

    public override Task OnReset()
    {
        _startedAtUtc = null;
        return Task.CompletedTask;
    }
}

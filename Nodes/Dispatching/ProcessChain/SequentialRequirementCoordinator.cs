using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

/// <summary>
/// Coordinates sequential capability dispatch by allowing the dispatcher to wait
/// until the collector recorded at least one offer for the current requirement.
/// </summary>
public class SequentialRequirementCoordinator
{
    private readonly Dictionary<string, TaskCompletionSource<bool>> _completionSources;

    public SequentialRequirementCoordinator(IList<CapabilityRequirement> requirements)
    {
        _completionSources = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);
        if (requirements == null)
        {
            return;
        }

        foreach (var requirement in requirements)
        {
            var requirementId = requirement?.RequirementId;
            if (string.IsNullOrWhiteSpace(requirementId))
            {
                continue;
            }

            _completionSources[requirementId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task<bool> WaitForCompletionAsync(string requirementId, TimeSpan timeout, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(requirementId))
        {
            return true;
        }

        if (!_completionSources.TryGetValue(requirementId, out var tcs))
        {
            return true;
        }

        var waitTask = tcs.Task;
        if (waitTask.IsCompleted)
        {
            return true;
        }

        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
        if (completed == waitTask)
        {
            return true;
        }

        logger?.LogWarning("SequentialRequirementCoordinator: timeout waiting for requirement {RequirementId}", requirementId);
        return false;
    }

    public void MarkRequirementCompleted(string requirementId)
    {
        if (string.IsNullOrWhiteSpace(requirementId))
        {
            return;
        }

        if (_completionSources.TryGetValue(requirementId, out var tcs))
        {
            tcs.TrySetResult(true);
        }
    }
}

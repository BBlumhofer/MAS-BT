using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using UAClient.Client;

namespace MAS_BT.Nodes.SkillControl;

/// <summary>
/// RetrySkill - Versucht fehlgeschlagene Skill-Ausführung erneut mit exponential backoff
/// </summary>
public class RetrySkillNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public string Parameters { get; set; } = string.Empty;
    public int MaxRetries { get; set; } = 3;
    public int BackoffMs { get; set; } = 1000;
    public bool ExponentialBackoff { get; set; } = true;

    public RetrySkillNode() : base("RetrySkill")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("RetrySkill: Starting retry for {SkillName} (max retries: {MaxRetries})", 
            SkillName, MaxRetries);

        try
        {
            var server = Context.Get<RemoteServer>("RemoteServer");
            if (server == null)
            {
                Logger.LogError("RetrySkill: No RemoteServer in context");
                return NodeStatus.Failure;
            }

            if (!server.Modules.TryGetValue(ModuleName, out var module))
            {
                Logger.LogError("RetrySkill: Module {ModuleName} not found", ModuleName);
                return NodeStatus.Failure;
            }

            if (!module.SkillSet.TryGetValue(SkillName, out var skill))
            {
                Logger.LogError("RetrySkill: Skill {SkillName} not found", SkillName);
                return NodeStatus.Failure;
            }

            int attemptCount = 0;
            int currentBackoffMs = BackoffMs;

            while (attemptCount < MaxRetries)
            {
                attemptCount++;
                Logger.LogInformation("RetrySkill: Attempt {Attempt}/{MaxRetries} for {SkillName}", 
                    attemptCount, MaxRetries, SkillName);

                try
                {
                    // Wenn Skill in Halted state, versuche zu reset
                    if (skill.CurrentState == UAClient.Common.SkillStates.Halted)
                    {
                        Logger.LogDebug("RetrySkill: Skill is halted, attempting reset");
                        if (skill is IAsyncCallable callable)
                        {
                            await callable.CallAsync("ResetAsync");
                        }
                    }

                    // Parsen der Parameter (comma-separated key=value)
                    var skillParams = ParseParameters(Parameters);

                    // Rufe ExecuteAsync mit Parametern auf
                    if (skill is IAsyncCallable execCallable)
                    {
                        await execCallable.CallAsync("ExecuteAsync", skillParams);
                        Logger.LogDebug("RetrySkill: ExecuteAsync called for {SkillName}", SkillName);
                    }
                    else
                    {
                        Logger.LogWarning("RetrySkill: Skill does not support async calls");
                        return NodeStatus.Failure;
                    }

                    // Warte auf Completion (mit Timeout)
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    int timeoutSeconds = 60;

                    while (stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
                    {
                        if (skill.CurrentState == UAClient.Common.SkillStates.Completed)
                        {
                            Logger.LogInformation("RetrySkill: Skill {SkillName} completed successfully on attempt {Attempt}", 
                                SkillName, attemptCount);
                            Set($"skill_{SkillName}_retry_attempts", attemptCount);
                            return NodeStatus.Success;
                        }

                        if (skill.CurrentState == UAClient.Common.SkillStates.Halted)
                        {
                            Logger.LogWarning("RetrySkill: Skill halted on attempt {Attempt}/{MaxRetries}", 
                                attemptCount, MaxRetries);
                            break; // Versuche nächste Iteration
                        }

                        await Task.Delay(500);
                    }

                    Logger.LogWarning("RetrySkill: Attempt {Attempt} timed out or failed", attemptCount);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "RetrySkill: Exception on attempt {Attempt}", attemptCount);
                }

                // Backoff vor nächstem Versuch
                if (attemptCount < MaxRetries)
                {
                    Logger.LogInformation("RetrySkill: Waiting {BackoffMs}ms before retry", currentBackoffMs);
                    await Task.Delay(currentBackoffMs);

                    if (ExponentialBackoff)
                    {
                        currentBackoffMs *= 2;
                    }
                }
            }

            Logger.LogError("RetrySkill: All {MaxRetries} retry attempts failed for {SkillName}", 
                MaxRetries, SkillName);
            Set($"skill_{SkillName}_retry_attempts", attemptCount);
            return NodeStatus.Failure;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RetrySkill: Error in retry logic");
            return NodeStatus.Failure;
        }
    }

    private Dictionary<string, string> ParseParameters(string paramString)
    {
        var result = new Dictionary<string, string>();
        
        if (string.IsNullOrWhiteSpace(paramString))
            return result;

        var pairs = paramString.Split(',');
        foreach (var pair in pairs)
        {
            var parts = pair.Trim().Split('=');
            if (parts.Length == 2)
            {
                result[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return result;
    }
}


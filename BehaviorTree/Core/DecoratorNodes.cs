using Microsoft.Extensions.Logging;

namespace MAS_BT.Core;

/// <summary>
/// Retry Node - Wiederholt Child bei Failure
/// </summary>
public class RetryNode : DecoratorNode
{
    /// <summary>
    /// Maximale Anzahl Versuche
    /// </summary>
    public int MaxRetries { get; set; } = 3;
    
    /// <summary>
    /// Backoff-Strategie: Wartezeit zwischen Versuchen (ms)
    /// </summary>
    public int BackoffMs { get; set; } = 100;
    
    /// <summary>
    /// Exponentielles Backoff aktivieren
    /// </summary>
    public bool ExponentialBackoff { get; set; } = true;
    
    public RetryNode() : base("Retry")
    {
    }
    
    public RetryNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            Logger.LogDebug("Retry '{Name}' attempt {Attempt}/{Max}", Name, attempt, MaxRetries);
            
            var result = await Child.Execute();
            
            if (result == NodeStatus.Success)
            {
                Logger.LogDebug("Retry '{Name}' succeeded on attempt {Attempt}", Name, attempt);
                return NodeStatus.Success;
            }
            
            if (result == NodeStatus.Running)
            {
                Logger.LogDebug("Retry '{Name}' child still running", Name);
                return NodeStatus.Running;
            }
            
            // Failure - retry mit Backoff
            if (attempt < MaxRetries)
            {
                int delay = ExponentialBackoff 
                    ? BackoffMs * (int)Math.Pow(2, attempt - 1)
                    : BackoffMs;
                
                Logger.LogDebug("Retry '{Name}' waiting {Delay}ms before retry", Name, delay);
                await Task.Delay(delay);
            }
        }
        
        Logger.LogWarning("Retry '{Name}' failed after {Max} attempts", Name, MaxRetries);
        return NodeStatus.Failure;
    }
}

/// <summary>
/// Timeout Node - Begrenzt Ausführungszeit des Childs
/// </summary>
public class TimeoutNode : DecoratorNode
{
    /// <summary>
    /// Timeout in Millisekunden
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;
    
    public TimeoutNode() : base("Timeout")
    {
    }
    
    public TimeoutNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("Timeout '{Name}' executing with {Timeout}ms limit", Name, TimeoutMs);
        
        using var cts = new CancellationTokenSource(TimeoutMs);
        
        try
        {
            var task = Child.Execute();
            
            // Warte mit Timeout
            if (await Task.WhenAny(task, Task.Delay(TimeoutMs, cts.Token)) == task)
            {
                var result = await task;
                Logger.LogDebug("Timeout '{Name}' child completed with {Result}", Name, result);
                return result;
            }
            else
            {
                Logger.LogWarning("Timeout '{Name}' exceeded {Timeout}ms - aborting child", Name, TimeoutMs);
                await Child.OnAbort();
                return NodeStatus.Failure;
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Timeout '{Name}' cancelled", Name);
            await Child.OnAbort();
            return NodeStatus.Failure;
        }
    }
}

/// <summary>
/// Repeat Node - Wiederholt Child N-mal oder unendlich
/// </summary>
public class RepeatNode : DecoratorNode
{
    /// <summary>
    /// Anzahl Wiederholungen
    /// -1 = unendlich
    /// </summary>
    public int Count { get; set; } = -1;
    
    /// <summary>
    /// Bei Failure stoppen?
    /// </summary>
    public bool StopOnFailure { get; set; } = true;
    
    private int _currentIteration = 0;
    
    public RepeatNode() : base("Repeat")
    {
    }
    
    public RepeatNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var infinite = Count < 0;

        // Tick-based repeat:
        // - Execute the child at most once per tick.
        // - If the child is Running, propagate Running.
        // - If the child succeeds, either advance iteration (finite) or keep running (infinite).
        // This matches BehaviorTree.CPP "tick" semantics and avoids blocking a single Execute() forever.

        var result = await Child.Execute();

        if (result == NodeStatus.Running)
        {
            return NodeStatus.Running;
        }

        if (result == NodeStatus.Failure)
        {
            if (StopOnFailure)
            {
                _currentIteration = 0;
                return NodeStatus.Failure;
            }

            // If we don't stop on failure, treat this iteration as "completed" and continue.
            // Reset child so the next tick starts a fresh attempt.
            await Child.OnReset();
            if (!infinite)
            {
                _currentIteration++;
                if (_currentIteration >= Count)
                {
                    _currentIteration = 0;
                    return NodeStatus.Success;
                }
            }

            return NodeStatus.Running;
        }

        // Success
        await Child.OnReset();

        if (infinite)
        {
            return NodeStatus.Running;
        }

        _currentIteration++;
        if (_currentIteration >= Count)
        {
            _currentIteration = 0;
            return NodeStatus.Success;
        }

        return NodeStatus.Running;
    }
    
    public override async Task OnAbort()
    {
        _currentIteration = 0;
        await base.OnAbort();
    }
    
    public override async Task OnReset()
    {
        _currentIteration = 0;
        await base.OnReset();
    }
}

/// <summary>
/// Inverter Node - Kehrt Ergebnis des Childs um
/// Success → Failure, Failure → Success
/// </summary>
public class InverterNode : DecoratorNode
{
    public InverterNode() : base("Inverter")
    {
    }
    
    public InverterNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var result = await Child.Execute();
        
        if (result == NodeStatus.Running)
        {
            return NodeStatus.Running;
        }
        
        var inverted = result == NodeStatus.Success ? NodeStatus.Failure : NodeStatus.Success;
        
        Logger.LogDebug("Inverter '{Name}' inverted {Original} → {Inverted}", 
            Name, result, inverted);
        
        return inverted;
    }
}

/// <summary>
/// Succeeder Node - Gibt immer Success zurück (außer Running)
/// </summary>
public class SucceederNode : DecoratorNode
{
    public SucceederNode() : base("Succeeder")
    {
    }
    
    public SucceederNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var result = await Child.Execute();
        
        if (result == NodeStatus.Running)
        {
            return NodeStatus.Running;
        }
        
        Logger.LogDebug("Succeeder '{Name}' forcing success (was {Original})", Name, result);
        return NodeStatus.Success;
    }
}

/// <summary>
/// RetryUntilSuccess Node - Wiederholt Child bis Success erreicht wird
/// Verwendet für unendliche Retry-Loops (z.B. Verbindungsaufbau)
/// </summary>
public class RetryUntilSuccessNode : DecoratorNode
{
    /// <summary>
    /// Maximale Anzahl Versuche (-1 = unendlich)
    /// </summary>
    public int NumAttempts { get; set; } = -1;
    
    /// <summary>
    /// Wartezeit zwischen Versuchen (ms)
    /// </summary>
    public int DelayMs { get; set; } = 1000;
    
    private int _currentAttempt = 0;
    
    public RetryUntilSuccessNode() : base("RetryUntilSuccess")
    {
    }
    
    public RetryUntilSuccessNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        bool infinite = NumAttempts < 0;
        
        Logger.LogDebug("RetryUntilSuccess '{Name}' starting ({Mode})", 
            Name, infinite ? "infinite" : $"max {NumAttempts} attempts");
        
        while (infinite || _currentAttempt < NumAttempts)
        {
            _currentAttempt++;
            
            Logger.LogDebug("RetryUntilSuccess '{Name}' attempt {Attempt}", Name, _currentAttempt);
            
            var result = await Child.Execute();
            
            if (result == NodeStatus.Success)
            {
                Logger.LogInformation("RetryUntilSuccess '{Name}' succeeded after {Attempt} attempts", 
                    Name, _currentAttempt);
                _currentAttempt = 0;
                return NodeStatus.Success;
            }
            
            if (result == NodeStatus.Running)
            {
                Logger.LogDebug("RetryUntilSuccess '{Name}' child still running", Name);
                return NodeStatus.Running;
            }
            
            // Failure - warte und retry
            if (infinite || _currentAttempt < NumAttempts)
            {
                Logger.LogDebug("RetryUntilSuccess '{Name}' waiting {Delay}ms before retry", 
                    Name, DelayMs);
                await Task.Delay(DelayMs);
            }
        }
        
        Logger.LogWarning("RetryUntilSuccess '{Name}' failed after {Max} attempts", 
            Name, NumAttempts);
        _currentAttempt = 0;
        return NodeStatus.Failure;
    }
    
    public override async Task OnAbort()
    {
        _currentAttempt = 0;
        await base.OnAbort();
    }
    
    public override async Task OnReset()
    {
        _currentAttempt = 0;
        await base.OnReset();
    }
}

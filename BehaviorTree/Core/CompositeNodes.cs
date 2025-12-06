using Microsoft.Extensions.Logging;

namespace MAS_BT.Core;

/// <summary>
/// Sequence Node - Führt Kinder sequenziell aus (stoppt bei erstem Failure)
/// Mit Memory: Merkt sich welches Kind gerade läuft (wie BehaviorTree.CPP)
/// </summary>
public class SequenceNode : CompositeNode
{
    private int _currentChildIndex = 0;
    
    public SequenceNode() : base("Sequence") {}
    public SequenceNode(string name) : base(name) {}
    
    public override async Task<NodeStatus> Execute()
    {
        // Starte bei dem Kind, wo wir beim letzten Tick aufgehört haben
        for (int i = _currentChildIndex; i < Children.Count; i++)
        {
            var child = Children[i];
            var result = await child.Execute();
            
            if (result == NodeStatus.Running)
            {
                // Kind läuft noch - merke Index für nächsten Tick
                _currentChildIndex = i;
                return NodeStatus.Running;
            }
            
            if (result == NodeStatus.Failure)
            {
                // Kind fehlgeschlagen - Reset für nächsten Durchlauf
                _currentChildIndex = 0;
                return NodeStatus.Failure;
            }
            
            // Kind erfolgreich - weiter zum nächsten
        }
        
        // Alle Kinder erfolgreich - Reset für nächsten Durchlauf
        _currentChildIndex = 0;
        return NodeStatus.Success;
    }
}

/// <summary>
/// Selector/Fallback Node - Führt Kinder aus bis eines Success zurückgibt
/// Mit Memory: Merkt sich welches Kind gerade läuft (wie BehaviorTree.CPP)
/// </summary>
public class SelectorNode : CompositeNode
{
    private int _currentChildIndex = 0;
    
    public SelectorNode() : base("Selector") {}
    public SelectorNode(string name) : base(name) {}
    
    public override async Task<NodeStatus> Execute()
    {
        // Starte bei dem Kind, wo wir beim letzten Tick aufgehört haben
        for (int i = _currentChildIndex; i < Children.Count; i++)
        {
            var child = Children[i];
            var result = await child.Execute();
            
            if (result == NodeStatus.Running)
            {
                // Kind läuft noch - merke Index für nächsten Tick
                _currentChildIndex = i;
                return NodeStatus.Running;
            }
            
            if (result == NodeStatus.Success)
            {
                // Kind erfolgreich - Reset für nächsten Durchlauf
                _currentChildIndex = 0;
                return NodeStatus.Success;
            }
            
            // Kind fehlgeschlagen - weiter zum nächsten
        }
        
        // Alle Kinder fehlgeschlagen - Reset für nächsten Durchlauf
        _currentChildIndex = 0;
        return NodeStatus.Failure;
    }
}

/// <summary>
/// Fallback ist ein Alias für Selector
/// </summary>
public class FallbackNode : SelectorNode
{
    public FallbackNode() : base("Fallback") {}
    public FallbackNode(string name) : base(name) {}
}

/// <summary>
/// Parallel Node - Führt alle Kinder parallel aus
/// </summary>
public class ParallelNode : CompositeNode
{
    public int SuccessThreshold { get; set; } = 1;
    public int FailureThreshold { get; set; } = 1;
    
    public ParallelNode() : base("Parallel") {}
    public ParallelNode(string name) : base(name) {}
    
    public override async Task<NodeStatus> Execute()
    {
        var tasks = Children.Select(c => c.Execute()).ToArray();
        var results = await Task.WhenAll(tasks);
        
        var successCount = results.Count(r => r == NodeStatus.Success);
        var failureCount = results.Count(r => r == NodeStatus.Failure);
        
        if (successCount >= SuccessThreshold)
            return NodeStatus.Success;
        if (failureCount >= FailureThreshold)
            return NodeStatus.Failure;
        
        return NodeStatus.Running;
    }
}

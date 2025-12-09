using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MAS_BT.Core;

/// <summary>
/// Status des Behavior Tree Node nach Ausführung
/// </summary>
public enum NodeStatus
{
    /// <summary>
    /// Node wurde erfolgreich ausgeführt
    /// </summary>
    Success,
    
    /// <summary>
    /// Node ist fehlgeschlagen
    /// </summary>
    Failure,
    
    /// <summary>
    /// Node läuft noch (asynchron)
    /// </summary>
    Running
}

/// <summary>
/// Basisklasse für alle Behavior Tree Nodes
/// </summary>
public abstract class BTNode
{
    /// <summary>
    /// Name des Nodes (für Logging und Debugging)
    /// </summary>
    public string Name { get; set; }
    
    /// <summary>
    /// Execution Context mit Shared State zwischen Nodes
    /// </summary>
    public BTContext Context { get; set; } = null!;
    
    /// <summary>
    /// Logger für diesen Node
    /// </summary>
    protected ILogger Logger { get; set; } = NullLogger.Instance;
    
    #region Blackboard Convenience Methods
    
    /// <summary>
    /// Direkter Zugriff auf das Blackboard (Convenience)
    /// </summary>
    protected IDictionary<string, object?> Blackboard => Context.Blackboard;
    
    /// <summary>
    /// Liest einen Wert aus dem Blackboard (typsicher)
    /// </summary>
    protected T? Get<T>(string key) => Context.Get<T>(key);
    
    /// <summary>
    /// Schreibt einen Wert ins Blackboard
    /// </summary>
    protected void Set(string key, object? value) => Context.Set(key, value);
    
    /// <summary>
    /// Prüft ob ein Key im Blackboard existiert
    /// </summary>
    protected bool Has(string key) => Context.Has(key);
    
    /// <summary>
    /// Ersetzt Placeholders im Format {VariableName} durch Werte aus dem Context/Blackboard
    /// Beispiel: "{MachineName}" → "ScrewingStation" (wenn Context.Get("MachineName") = "ScrewingStation")
    /// </summary>
    protected string ResolvePlaceholders(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("{"))
            return input;
        
        var result = input;
        var startIndex = 0;
        
        while (startIndex < result.Length)
        {
            var openBrace = result.IndexOf('{', startIndex);
            if (openBrace == -1)
                break;
            
            var closeBrace = result.IndexOf('}', openBrace);
            if (closeBrace == -1)
                break;
            
            var placeholder = result.Substring(openBrace + 1, closeBrace - openBrace - 1);
            var replacement = Context.Get<string>(placeholder);
            
            if (replacement != null)
            {
                result = result.Substring(0, openBrace) + replacement + result.Substring(closeBrace + 1);
                startIndex = openBrace + replacement.Length;
            }
            else
            {
                // Placeholder nicht gefunden - behalte Original
                startIndex = closeBrace + 1;
            }
        }
        
        return result;
    }
    
    #endregion
    
    protected BTNode(string name)
    {
        Name = name;
    }
    
    /// <summary>
    /// Setzt den Logger für diesen Node (für BehaviorTreeLoader)
    /// </summary>
    public void SetLogger(ILogger logger)
    {
        Logger = logger ?? NullLogger.Instance;
    }
    
    /// <summary>
    /// Initialisiert den Node mit Context und Logger
    /// </summary>
    public virtual void Initialize(BTContext context, ILogger logger)
    {
        Context = context;
        Logger = logger ?? NullLogger.Instance;
    }
    
    /// <summary>
    /// Hauptausführungsmethode des Nodes
    /// </summary>
    public abstract Task<NodeStatus> Execute();
    
    /// <summary>
    /// Wird aufgerufen wenn der Node abgebrochen wird
    /// </summary>
    public virtual Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Wird aufgerufen wenn der Node zurückgesetzt wird
    /// </summary>
    public virtual Task OnReset()
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Basisklasse für Composite Nodes (Sequence, Selector, Parallel)
/// </summary>
public abstract class CompositeNode : BTNode
{
    public List<BTNode> Children { get; set; } = new();
    
    protected CompositeNode(string name) : base(name) { }
    
    /// <summary>
    /// Fügt ein Kind hinzu
    /// </summary>
    public void AddChild(BTNode child)
    {
        Children.Add(child);
    }
    
    public override async Task OnAbort()
    {
        foreach (var child in Children)
        {
            await child.OnAbort();
        }
    }
    
    public override async Task OnReset()
    {
        foreach (var child in Children)
        {
            await child.OnReset();
        }
    }
}

/// <summary>
/// Basisklasse für Decorator Nodes (Retry, Timeout, Inverter, etc.)
/// </summary>
public abstract class DecoratorNode : BTNode
{
    public BTNode Child { get; set; } = null!;
    
    protected DecoratorNode(string name) : base(name) { }
    
    public override async Task OnAbort()
    {
        await Child.OnAbort();
    }
    
    public override async Task OnReset()
    {
        await Child.OnReset();
    }
}

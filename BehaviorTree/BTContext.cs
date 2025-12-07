using Microsoft.Extensions.Logging;

namespace MAS_BT.Core;

/// <summary>
/// Blackboard-Kontext für Behavior Trees
/// Speichert geteilte Daten zwischen Nodes
/// </summary>
public class BTContext
{
    private readonly Dictionary<string, object?> _data = new();
    private readonly ILogger<BTContext>? _logger;

    /// <summary>
    /// Agent ID für diesen Context
    /// </summary>
    public string AgentId { get; set; } = "UnknownAgent";
    
    /// <summary>
    /// Agent Role/Type
    /// </summary>
    public string AgentRole { get; set; } = "UnknownRole";

    public BTContext(ILogger<BTContext>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Direkter Zugriff auf das interne Dictionary (Blackboard-Pattern)
    /// </summary>
    public IDictionary<string, object?> Blackboard => _data;

    /// <summary>
    /// Speichert einen Wert im Context
    /// </summary>
    public void Set(string key, object? value)
    {
        _data[key] = value;
        _logger?.LogDebug("Context: {Key} = {Value}", key, value);
    }

    /// <summary>
    /// Holt einen Wert aus dem Context
    /// </summary>
    public object? Get(string key)
    {
        _data.TryGetValue(key, out var value);
        return value;
    }

    /// <summary>
    /// Holt einen typisierten Wert aus dem Context
    /// </summary>
    public T? Get<T>(string key)
    {
        if (_data.TryGetValue(key, out var value))
        {
            if (value is T typedValue)
                return typedValue;
            
            // Versuche Konvertierung
            try
            {
                if (value != null)
                    return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                _logger?.LogWarning("Fehler beim Konvertieren von '{Key}' zu Typ {Type}", key, typeof(T).Name);
            }
        }
        
        return default;
    }

    /// <summary>
    /// Entfernt einen Wert aus dem Context
    /// </summary>
    public void Remove(string key)
    {
        _data.Remove(key);
    }

    /// <summary>
    /// Prüft ob ein Key existiert
    /// </summary>
    public bool Has(string key)
    {
        return _data.ContainsKey(key);
    }

    /// <summary>
    /// Löscht alle Werte
    /// </summary>
    public void Clear()
    {
        _data.Clear();
    }

    /// <summary>
    /// Gibt alle Keys zurück
    /// </summary>
    public IEnumerable<string> Keys => _data.Keys;

}

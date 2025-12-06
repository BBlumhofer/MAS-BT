using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Nodes.Configuration;
using MAS_BT.Nodes.Locking;
using MAS_BT.Nodes.Messaging;
using MAS_BT.Nodes.Monitoring;
using MAS_BT.Nodes.Skills;
using MAS_BT.Nodes.Core;
using MAS_BT.Nodes; // Neue Monitoring Nodes

namespace MAS_BT.Serialization;

/// <summary>
/// Node Registry - Verwaltet alle verfügbaren BT Node Typen
/// </summary>
public class NodeRegistry
{
    private readonly Dictionary<string, Type> _nodeTypes = new();
    private readonly ILogger _logger;
    
    public NodeRegistry(ILogger logger)
    {
        _logger = logger;
        RegisterDefaultNodes();
    }
    
    /// <summary>
    /// Registriert einen Node-Typ unter einem Namen
    /// </summary>
    public void Register<T>(string name) where T : BTNode
    {
        _nodeTypes[name] = typeof(T);
        _logger.LogDebug("Registriert Node: {Name} → {Type}", name, typeof(T).Name);
    }
    
    /// <summary>
    /// Registriert einen Node-Typ (Name = Klassenname ohne "Node" Suffix)
    /// </summary>
    public void Register<T>() where T : BTNode
    {
        var name = typeof(T).Name;
        if (name.EndsWith("Node"))
            name = name.Substring(0, name.Length - 4);
        
        Register<T>(name);
    }
    
    /// <summary>
    /// Erstellt eine Node-Instanz anhand des Namens
    /// </summary>
    public BTNode CreateNode(string name)
    {
        if (!_nodeTypes.TryGetValue(name, out var type))
        {
            throw new InvalidOperationException($"Unbekannter Node-Typ: {name}. Verfügbare Typen: {string.Join(", ", _nodeTypes.Keys)}");
        }
        
        // Versuche Constructor mit ILogger Parameter
        var ctorWithLogger = type.GetConstructor(new[] { typeof(ILogger) });
        if (ctorWithLogger != null)
        {
            var node = ctorWithLogger.Invoke(new object[] { _logger }) as BTNode;
            if (node == null)
            {
                throw new InvalidOperationException($"Fehler beim Erstellen von Node mit Logger: {name}");
            }
            return node;
        }
        
        // Fallback: Parameter-loser Constructor
        var node2 = Activator.CreateInstance(type) as BTNode;
        if (node2 == null)
        {
            throw new InvalidOperationException($"Fehler beim Erstellen von Node: {name}");
        }
        
        // Setze Logger via Property falls vorhanden
        var loggerProp = type.GetProperty("Logger");
        if (loggerProp != null && loggerProp.CanWrite)
        {
            loggerProp.SetValue(node2, _logger);
        }
        
        return node2;
    }
    
    /// <summary>
    /// Prüft ob ein Node-Typ registriert ist
    /// </summary>
    public bool IsRegistered(string name) => _nodeTypes.ContainsKey(name);
    
    /// <summary>
    /// Gibt alle registrierten Node-Namen zurück
    /// </summary>
    public IEnumerable<string> GetRegisteredNames() => _nodeTypes.Keys;
    
    /// <summary>
    /// Registriert alle Standard-Nodes (Composite + Decorator)
    /// </summary>
    private void RegisterDefaultNodes()
    {
        // Core Composite Nodes
        Register<SequenceNode>();
        Register<SelectorNode>();
        Register<SelectorNode>("Fallback"); // Alias für Selector
        Register<ParallelNode>();
        
        // Core Decorator Nodes
        Register<RetryNode>();
        Register<TimeoutNode>();
        Register<RepeatNode>();
        Register<InverterNode>();
        Register<SucceederNode>();
        Register<RetryUntilSuccessNode>();
        
        // Core Utility Nodes
        Register<WaitNode>();
        Register<SetBlackboardValueNode>();
        
        // Condition Nodes
        Register<ConditionNode>();
        Register<BlackboardConditionNode>();
        Register<CompareConditionNode>();
        
        // Configuration Nodes
        Register<ReadConfigNode>();
        Register<ConnectToMessagingBrokerNode>();
        Register<ReadShellNode>();
        Register<ReadCapabilityDescriptionNode>("ReadCapabilityDescription");
        Register<ReadSkillsNode>("ReadSkills");
        Register<ReadMachineScheduleNode>("ReadMachineSchedule");
        Register<ReadNameplateNode>("ReadNameplate");
        Register<ConnectToModuleNode>();
        Register<CoupleModuleNode>();
        
        // Locking Nodes
        Register<LockResourceNode>("LockResource");
        Register<UnlockResourceNode>("UnlockResource");
        Register<CheckLockStatusNode>();
        
        // Messaging Nodes
        Register<SendMessageNode>();
        Register<SendLogMessageNode>();
        Register<SendConfigAsLogNode>();
        Register<WaitForMessageNode>();
        
        // Monitoring Nodes (bestehende)
        Register<ReadStorageNode>();
        Register<CheckStartupSkillStatusNode>();
        
        // Monitoring Nodes (Phase 1 - Core Monitoring)
        Register<CheckReadyStateNode>();
        Register<CheckErrorStateNode>();
        Register<CheckLockedStateNode>();
        Register<MonitoringSkillNode>();
        
        // Skill Nodes
        Register<ExecuteSkillNode>();
        
        _logger.LogInformation("Standard Nodes registriert: {Count}", _nodeTypes.Count);
    }
}

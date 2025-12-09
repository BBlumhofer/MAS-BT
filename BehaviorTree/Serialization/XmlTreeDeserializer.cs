using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;

namespace MAS_BT.Serialization;

/// <summary>
/// XML Tree Deserializer - Lädt BT aus XML-Dateien
/// </summary>
public class XmlTreeDeserializer
{
    private readonly NodeRegistry _registry;
    private readonly ILogger _logger;
    private Dictionary<string, XElement> _behaviorTrees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _subTreeStack = new();
    
    public XmlTreeDeserializer(NodeRegistry registry, ILogger logger)
    {
        _registry = registry;
        _logger = logger;
    }
    
    /// <summary>
    /// Lädt einen Behavior Tree aus XML-Datei (Groot2 BehaviorTree.CPP V4 Format)
    /// </summary>
    public BTNode Deserialize(string xmlPath, BTContext context)
    {
        _logger.LogInformation("Lade Behavior Tree aus: {Path}", xmlPath);
        
        if (!File.Exists(xmlPath))
        {
            throw new FileNotFoundException($"XML-Datei nicht gefunden: {xmlPath}");
        }
        
        var doc = XDocument.Load(xmlPath);
        var root = doc.Root;
        
        if (root == null)
        {
            throw new InvalidOperationException("XML muss ein Root Element haben");
        }
        
        // Groot2 BehaviorTree.CPP V4 Format validation
        if (!root.Name.LocalName.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Ungültiges Root Element: '{root.Name.LocalName}'. " +
                "Erwartet: <root BTCPP_format=\"4\" main_tree_to_execute=\"TreeName\"> (Groot2 BehaviorTree.CPP V4)");
        }
        
        // Check BehaviorTree.CPP version
        var btcppFormat = root.Attribute("BTCPP_format")?.Value;
        if (btcppFormat != null && btcppFormat != "4")
        {
            _logger.LogWarning("BTCPP_format={Format} detected. Recommended: V4", btcppFormat);
        }
        
        // Get main tree name
        var mainTreeName = root.Attribute("main_tree_to_execute")?.Value;
        if (string.IsNullOrEmpty(mainTreeName))
        {
            _logger.LogWarning("main_tree_to_execute attribute is missing. Using the first available BehaviorTree.");
        }
        
        _behaviorTrees = BuildBehaviorTreeIndex(root);

        _logger.LogInformation("Lade Tree (Groot2 V4): {TreeName}", mainTreeName);
        
        // Find BehaviorTree with matching ID
        var behaviorTreeElement = root.Elements("BehaviorTree")
            .FirstOrDefault(bt => bt.Attribute("ID")?.Value == mainTreeName);
        
        if (behaviorTreeElement == null)
        {
            // Fallback: wenn main_tree_to_execute fehlt, nimm ersten BehaviorTree
            behaviorTreeElement = root.Elements("BehaviorTree").FirstOrDefault();
            
            if (behaviorTreeElement == null)
            {
                throw new InvalidOperationException(
                    $"No BehaviorTree found. " +
                    "Available elements: " + string.Join(", ", 
                        root.Elements().Select(e => e.Name.LocalName)));
            }
            
            mainTreeName = behaviorTreeElement.Attribute("ID")?.Value ?? "UnnamedTree";
            _logger.LogInformation("Using first available tree: {TreeName}", mainTreeName);
        }
        
        // Get root node (first child element of BehaviorTree)
        var rootNodeElement = behaviorTreeElement.Elements().FirstOrDefault();
        if (rootNodeElement == null)
        {
            throw new InvalidOperationException(
                $"BehaviorTree '{mainTreeName}' must contain at least one root node");
        }
        
        var rootNode = DeserializeNode(rootNodeElement, context);
        _subTreeStack.Clear();
        _behaviorTrees.Clear();
        
        _logger.LogInformation("✓ Behavior Tree '{TreeName}' erfolgreich geladen (Groot2 V4)", mainTreeName);
        return rootNode;
    }
    
    /// <summary>
    /// Deserialisiert alle Kinder eines Composite Nodes
    /// </summary>
    private List<BTNode> DeserializeChildren(XElement parentElement, BTContext context)
    {
        return parentElement.Elements()
            .Select(childElement => DeserializeNode(childElement, context))
            .ToList();
    }
    
    /// <summary>
    /// Deserialisiert das einzelne Kind eines Decorator Nodes
    /// </summary>
    private BTNode DeserializeSingleChild(XElement parentElement, BTContext context)
    {
        var childElement = parentElement.Elements().FirstOrDefault();
        if (childElement == null)
        {
            throw new InvalidOperationException($"Decorator Node '{parentElement.Name}' muss genau ein Kind-Element haben");
        }
        
        return DeserializeNode(childElement, context);
    }
    
    /// <summary>
    /// Setzt Properties aus XML Attributen (mit Config-Interpolation)
    /// </summary>
    private void SetPropertiesFromAttributes(BTNode node, XElement element)
    {
        var nodeType = node.GetType();
        
        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName == "name")
                continue; // Name wird separat behandelt
            
            var propName = ToPascalCase(attr.Name.LocalName);
            var prop = nodeType.GetProperty(propName);
            
            if (prop != null && prop.CanWrite)
            {
                try {
                    var value = InterpolateConfigValues(attr.Value, node.Context);
                    var convertedValue = ConvertValue(value, prop.PropertyType);
                    prop.SetValue(node, convertedValue);
                    
                    _logger.LogTrace("Property gesetzt: {Node}.{Property} = {Value}", 
                        node.Name, propName, convertedValue);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fehler beim Setzen von Property {Property} auf Node {Node}", 
                        propName, node.Name);
                }
            }
        }
    }
    
    /// <summary>
    /// Interpoliert Config-Werte in Strings (z.B. {config.OPCUA.Endpoint})
    /// </summary>
    private string InterpolateConfigValues(string value, BTContext context)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("{"))
            return value;
        
        var result = value;
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
            var replacement = context.Get<string>(placeholder) ?? $"{{{placeholder}}}";
            
            result = result.Substring(0, openBrace) + replacement + result.Substring(closeBrace + 1);
            startIndex = openBrace + replacement.Length;
            
            _logger.LogTrace("Config-Interpolation: {{{Placeholder}}} → {Value}", placeholder, replacement);
        }
        
        return result;
    }
    
    /// <summary>
    /// Konvertiert String-Wert zum Zieltyp
    /// </summary>
    private object? ConvertValue(string value, Type targetType)
    {
        if (targetType == typeof(string))
            return value;
        
        if (targetType == typeof(int))
            return int.Parse(value);
        
        if (targetType == typeof(double))
            return double.Parse(value);
        
        if (targetType == typeof(bool))
            return bool.Parse(value);
        
        if (targetType == typeof(long))
            return long.Parse(value);
        
        return value;
    }
    
    /// <summary>
    /// Konvertiert kebab-case zu PascalCase (z.B. "timeout-ms" → "TimeoutMs")
    /// </summary>
    private string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;
        
        // Behandle verschiedene Naming Conventions
        var parts = input.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length == 1)
        {
            // Einfacher Name: Ersten Buchstaben groß
            return char.ToUpper(parts[0][0]) + parts[0].Substring(1);
        }
        
        // Mehrere Teile: Jeden Teil mit Großbuchstaben beginnen
        return string.Join("", parts.Select(p => char.ToUpper(p[0]) + p.Substring(1)));
    }
    
    /// <summary>
    /// Deserialisiert einen einzelnen Node aus XML Element
    /// </summary>
    private BTNode DeserializeNode(XElement element, BTContext context)
    {
        if (element.Name.LocalName.Equals("SubTree", StringComparison.OrdinalIgnoreCase))
        {
            return ExpandSubTree(element, context);
        }
        
        var nodeTypeName = element.Name.LocalName;
        var nodeName = element.Attribute("name")?.Value ?? nodeTypeName;
        
        _logger.LogTrace("Deserialisiere Node: {NodeType}", nodeTypeName);
        
        // Erstelle Node über Registry
        var node = _registry.CreateNode(nodeTypeName);
        
        // Setze Name, Context und Logger
        node.Name = nodeName;
        node.Context = context;
        node.SetLogger(_logger);
        
        // Setze Properties aus XML Attributen
        SetPropertiesFromAttributes(node, element);
        
        // Behandle Kinder je nach Node-Typ
        if (node is Core.CompositeNode compositeNode)
        {
            compositeNode.Children = DeserializeChildren(element, context);
        }
        else if (node is Core.DecoratorNode decoratorNode)
        {
            decoratorNode.Child = DeserializeSingleChild(element, context);
        }
        
        return node;
    }

    /// <summary>
    /// Baut ein Dictionary aller BehaviorTree-Definitionen für SubTree-Expansion auf
    /// </summary>
    private Dictionary<string, XElement> BuildBehaviorTreeIndex(XElement root)
    {
        var map = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var btElement in root.Elements("BehaviorTree"))
        {
            var id = btElement.Attribute("ID")?.Value;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (map.ContainsKey(id))
            {
                _logger.LogWarning("Duplicate BehaviorTree ID detected: {Id}. Using last definition.", id);
            }
            map[id] = btElement;
        }

        return map;
    }

    /// <summary>
    /// Ersetzt ein <SubTree>-Element durch den referenzierten BehaviorTree
    /// </summary>
    private BTNode ExpandSubTree(XElement element, BTContext context)
    {
        var subtreeId = element.Attribute("ID")?.Value ?? element.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(subtreeId))
        {
            throw new InvalidOperationException("SubTree node requires an ID attribute");
        }

        if (_subTreeStack.Contains(subtreeId))
        {
            var cycle = string.Join(" → ", _subTreeStack.Reverse().Concat(new[] { subtreeId }));
            throw new InvalidOperationException($"Detected recursive SubTree reference: {cycle}");
        }

        if (!_behaviorTrees.TryGetValue(subtreeId, out var subtreeElement))
        {
            throw new InvalidOperationException(
                $"SubTree '{subtreeId}' not found. Available trees: {string.Join(", ", _behaviorTrees.Keys)}");
        }

        var rootElement = subtreeElement.Elements().FirstOrDefault();
        if (rootElement == null)
        {
            throw new InvalidOperationException($"SubTree '{subtreeId}' must contain a root node");
        }

        if (element.Elements().Any(e => e.Name.LocalName.Equals("remap", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("SubTree remapping elements are not supported yet for '{SubTree}' and will be ignored.", subtreeId);
        }

        _subTreeStack.Push(subtreeId);
        var clonedRoot = new XElement(rootElement);

        var customName = element.Attribute("name")?.Value;
        if (!string.IsNullOrWhiteSpace(customName))
        {
            clonedRoot.SetAttributeValue("name", customName);
        }

        try
        {
            return DeserializeNode(clonedRoot, context);
        }
        finally
        {
            _subTreeStack.Pop();
        }
    }
}

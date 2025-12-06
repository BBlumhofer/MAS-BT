using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Serialization;

/// <summary>
/// Konvertiert MAS-BT XML Format zu Groot-kompatiblem Format und zurück
/// </summary>
public class GrootXmlConverter
{
    private readonly ILogger _logger;
    
    public GrootXmlConverter(ILogger logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Konvertiert MAS-BT Format zu Groot Format
    /// Aus: <BehaviorTree><Root name="X"><Sequence>...</Sequence></Root></BehaviorTree>
    /// Nach: <root main_tree_to_execute="X"><BehaviorTree ID="X"><Sequence>...</Sequence></BehaviorTree></root>
    /// </summary>
    public void ConvertToGroot(string inputPath, string outputPath)
    {
        _logger.LogInformation("Konvertiere {Input} zu Groot-Format → {Output}", inputPath, outputPath);
        
        var doc = XDocument.Load(inputPath);
        var behaviorTree = doc.Root;
        
        if (behaviorTree?.Name != "BehaviorTree")
        {
            throw new InvalidOperationException("Root element muss <BehaviorTree> sein");
        }
        
        // Finde Root-Node
        var rootNode = behaviorTree.Element("Root");
        if (rootNode == null)
        {
            throw new InvalidOperationException("Kein <Root> Element gefunden");
        }
        
        var treeName = rootNode.Attribute("name")?.Value ?? "MainTree";
        
        // Erstelle Groot-kompatible Struktur
        var grootRoot = new XElement("root",
            new XAttribute("main_tree_to_execute", treeName)
        );
        
        // Erstelle BehaviorTree mit ID
        var grootBehaviorTree = new XElement("BehaviorTree",
            new XAttribute("ID", treeName)
        );
        
        // Kopiere alle Kinder vom Root-Node (außer dem Root selbst)
        foreach (var child in rootNode.Elements())
        {
            grootBehaviorTree.Add(new XElement(child));
        }
        
        grootRoot.Add(grootBehaviorTree);
        
        // Speichere Groot-kompatibles XML
        var grootDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            grootRoot
        );
        
        grootDoc.Save(outputPath);
        _logger.LogInformation("✓ Groot-XML gespeichert: {Output}", outputPath);
    }
    
    /// <summary>
    /// Konvertiert Groot Format zurück zu MAS-BT Format
    /// </summary>
    public void ConvertFromGroot(string inputPath, string outputPath)
    {
        _logger.LogInformation("Konvertiere {Input} von Groot-Format → {Output}", inputPath, outputPath);
        
        var doc = XDocument.Load(inputPath);
        var root = doc.Root;
        
        if (root?.Name != "root")
        {
            throw new InvalidOperationException("Groot-Datei muss mit <root> beginnen");
        }
        
        var mainTreeName = root.Attribute("main_tree_to_execute")?.Value ?? "MainTree";
        
        // Finde BehaviorTree Element
        var behaviorTree = root.Element("BehaviorTree");
        if (behaviorTree == null)
        {
            throw new InvalidOperationException("Kein <BehaviorTree> Element in Groot-Datei gefunden");
        }
        
        // Erstelle MAS-BT Struktur
        var masBtRoot = new XElement("BehaviorTree");
        var masBtRootNode = new XElement("Root",
            new XAttribute("name", mainTreeName)
        );
        
        // Kopiere alle Kinder
        foreach (var child in behaviorTree.Elements())
        {
            masBtRootNode.Add(new XElement(child));
        }
        
        masBtRoot.Add(masBtRootNode);
        
        // Speichere MAS-BT Format
        var masBtDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            masBtRoot
        );
        
        masBtDoc.Save(outputPath);
        _logger.LogInformation("✓ MAS-BT XML gespeichert: {Output}", outputPath);
    }
    
    /// <summary>
    /// Konvertiert alle .bt.xml Dateien in einem Verzeichnis zu Groot-Format
    /// </summary>
    public void ConvertDirectoryToGroot(string treesDir, string grootOutputDir)
    {
        if (!Directory.Exists(treesDir))
        {
            throw new DirectoryNotFoundException($"Trees Directory nicht gefunden: {treesDir}");
        }
        
        Directory.CreateDirectory(grootOutputDir);
        
        var btFiles = Directory.GetFiles(treesDir, "*.bt.xml", SearchOption.AllDirectories);
        
        _logger.LogInformation("Konvertiere {Count} BT-Dateien zu Groot-Format", btFiles.Length);
        
        foreach (var btFile in btFiles)
        {
            var relativePath = Path.GetRelativePath(treesDir, btFile);
            var grootFile = Path.Combine(grootOutputDir, relativePath);
            
            var grootFileDir = Path.GetDirectoryName(grootFile);
            if (!string.IsNullOrEmpty(grootFileDir))
            {
                Directory.CreateDirectory(grootFileDir);
            }
            
            try
            {
                ConvertToGroot(btFile, grootFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fehler beim Konvertieren von {File}", btFile);
            }
        }
        
        _logger.LogInformation("✓ Konvertierung abgeschlossen. Groot-Trees in: {Dir}", grootOutputDir);
    }
}

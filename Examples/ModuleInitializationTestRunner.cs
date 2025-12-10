using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Serialization;
using MAS_BT.Services;
using System.Text.Json;
using System.Linq;
using UAClient.Client;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;

namespace MAS_BT.Examples;

public class ModuleInitializationTestRunner
{
    public static async Task Run(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║    MAS-BT: Module Initialization Test Debug Runner          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        // Lade Config: wenn ein JSON-Pfad als erstes Argument übergeben wurde, verwende diesen
        string? providedConfigPath = null;
        if (args != null && args.Length > 0)
        {
            // akzeptiere z.B. `dotnet run -- configs/planning_agent.json`
            providedConfigPath = args.Select(a => a?.Trim()).FirstOrDefault(a => !string.IsNullOrEmpty(a) && (a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || a.Contains("configs")) && File.Exists(a));
        }

        var config = LoadConfig(providedConfigPath);
        var opcuaEndpoint = GetConfigValue(config, "OPCUA.Endpoint", "opc.tcp://192.168.178.30:4849");
        var opcuaUsername = GetConfigValue(config, "OPCUA.Username", "orchestrator");
        var opcuaPassword = GetConfigValue(config, "OPCUA.Password", "orchestrator");
        var moduleId = GetConfigValue(config, "Agent.ModuleId", "Module_Assembly_01");
        var agentId = GetConfigValue(config, "Agent.AgentId", "TestAgent");
        var agentRole = GetConfigValue(config, "Agent.Role", "ResourceHolon");
        var mqttBroker = GetConfigValue(config, "MQTT.Broker", "localhost");
        var mqttPort = GetConfigInt(config, "MQTT.Port", 1883);
        var preconditionRetries = GetConfigInt(config, "Execution.MaxPreconditionRetries", 10);
        var preconidionBackoffTime = GetConfigInt(config, "Execution.PreconditionBackoffStartMs", 20000);
        
        Console.WriteLine($"📋 Configuration:");
        Console.WriteLine($"   OPC UA Endpoint: {opcuaEndpoint}");
        Console.WriteLine($"   OPC UA Username: {opcuaUsername}");
        Console.WriteLine($"   Module ID: {moduleId}");
        Console.WriteLine($"   Agent ID: {agentId}");
        Console.WriteLine($"   MQTT Broker: {mqttBroker}:{mqttPort}");
        Console.WriteLine();
        
        // Shared BTContext (AgentId/Role can change based on config nodes)
        var context = new BTContext();

        // MQTT Client erstellen (optional - nur wenn MQTT verfügbar)
        MessagingClient? messagingClient = null;
        try
        {
            Console.WriteLine("🔌 Connecting to MQTT Broker...");
            var mqttTransport = new MqttTransport(mqttBroker, mqttPort, agentId);
            messagingClient = new MessagingClient(mqttTransport, $"{agentId}/logs");
            await messagingClient.ConnectAsync();
            Console.WriteLine("✓ MQTT Connected - Logs werden automatisch via MQTT gesendet");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  MQTT Connection failed: {ex.Message}");
            Console.WriteLine("   → Logs werden nur auf Console ausgegeben");
        }
        Console.WriteLine();
        
        // Logger mit MQTT-Integration erstellen (AgentId/Role werden dynamisch aus dem Context gelesen)
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            // Standard-Log-Level auf Information setzen - Debug/Trace sind standardmäßig aus
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new MqttLoggerProvider(
                messagingClient,
                () => context.AgentId,
                () => context.AgentRole));
        });
        
        var logger = loggerFactory.CreateLogger<ModuleInitializationTestRunner>();
        
        // BTContext Logger erst nach Factory-Erzeugung setzen
        context = new BTContext(loggerFactory.CreateLogger<BTContext>())
        {
            AgentId = agentId,
            AgentRole = agentRole
        };
        
        context.Set("config.OPCUA.Endpoint", opcuaEndpoint);
        context.Set("config.OPCUA.Username", opcuaUsername);
        context.Set("config.OPCUA.Password", opcuaPassword);
        context.Set("config.Agent.ModuleId", moduleId);
        context.Set("config.MQTT.Broker", mqttBroker);
        context.Set("config.MQTT.Port", mqttPort);
        // Alias für Legacy-Nodes: einige Nodes lesen direkt 'ModuleId' aus dem Context
        context.Set("ModuleId", moduleId);
        context.Set("AgentId", agentId);

        // Execution queue keeps full SkillRequests (conversation/product IDs stay intact)
        context.Set("SkillRequestQueue", new SkillRequestQueue());
        // Precondition retry configuration: default to 10 retries and 5 minutes start timeout
        context.Set("MaxPreconditionRetries", preconditionRetries);
        context.Set("PreconditionBackoffStartMs", preconidionBackoffTime); // 5 minutes in ms
        
        // MessagingClient im Context speichern (für SendLogMessage Nodes - falls noch vorhanden)
        if (messagingClient != null)
        {
            context.Set("MessagingClient", messagingClient);
        }
        
        Console.WriteLine("🔧 Setup:");
        Console.WriteLine("   ✓ BTContext erstellt");
        Console.WriteLine("   ✓ Config-Werte gesetzt (inkl. Username/Password)");
        Console.WriteLine("   ✓ MqttLogger aktiviert - alle Logs ab INFO werden automatisch via MQTT gesendet");
        Console.WriteLine("   → ConnectToModule wird den UaClient mit Credentials erstellen");
        Console.WriteLine();
        
        try
        {
            // Bestimme Behavior-Tree-Pfad:
            // 1. aus der geladenen Config (z.B. top-level "InitializationTree" oder unter "ProductAgent.InitializationTree")
            // 2. falls keine Angabe in der Config vorhanden ist, versuche Pfad aus CLI-Argumenten (z.B. direkte BT-Datei)
            // 3. Fallback: wähle eine tree-Datei basierend auf dem Config-Dateinamen (planning/execution/product)
            string? btFilePath = null;

            btFilePath = GetInitializationTreeFromConfig(config);

            if (string.IsNullOrWhiteSpace(btFilePath))
            {
                btFilePath = ResolveBehaviorTreePath(args);
            }

            if (string.IsNullOrWhiteSpace(btFilePath) && !string.IsNullOrWhiteSpace(providedConfigPath))
            {
                var lower = Path.GetFileName(providedConfigPath).ToLowerInvariant();
                if (lower.Contains("planning")) btFilePath = "Trees/PlanningAgent.bt.xml";
                else if (lower.Contains("execution") || lower.Contains("agent_execution") || lower.Contains("execution_agent")) btFilePath = "Trees/ExecutionAgent.bt.xml";
                else if (lower.Contains("product")) btFilePath = "Trees/ProductAgentInitialization.bt.xml";
            }

            btFilePath ??= "Trees/Init_and_ExecuteSkill.bt.xml";
            
            if (!File.Exists(btFilePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ BT Datei nicht gefunden: {btFilePath}");
                Console.WriteLine("   → Nutze 'dotnet run -- Trees/Examples/MyTree.bt.xml' oder 'dotnet run --Examples/MyTree.bt.xml'");
                Console.ResetColor();
                return;
            }
            
            Console.WriteLine($"🌳 Lade Behavior Tree: {btFilePath}");
            Console.WriteLine();
            
            var registry = new NodeRegistry(loggerFactory.CreateLogger<NodeRegistry>());
            var deserializer = new XmlTreeDeserializer(registry, loggerFactory.CreateLogger<XmlTreeDeserializer>());
            
            var rootNode = deserializer.Deserialize(btFilePath, context);
            
            Console.WriteLine($"✓ BT geladen: {rootNode.Name}");
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────────────────────────────");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("🚀 STARTE BEHAVIOR TREE EXECUTION");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────────────────────────────");
            Console.WriteLine();
            
            // Event Loop für kontinuierliche Ausführung (wie BehaviorTree.CPP Tick)
            var keepRunning = true;
            var tickCount = 0;
            var startTime = DateTime.UtcNow;
            const int tickPeriodMs = 100; // 10 Hz Tick-Rate (wie BehaviorTree.CPP Standard)
            
            // Ctrl+C Handler - NUR EINMAL registrieren
            var ctrlCPressed = false;
            ConsoleCancelEventHandler? cancelHandler = null;
            cancelHandler = (sender, e) =>
            {
                if (!ctrlCPressed)
                {
                    e.Cancel = true;
                    keepRunning = false;
                    ctrlCPressed = true;
                    Console.WriteLine();
                    Console.WriteLine("⚠️  Ctrl+C detected - Stopping execution loop...");
                }
            };
            Console.CancelKeyPress += cancelHandler;
            
            NodeStatus result = NodeStatus.Running;
            
            Console.WriteLine($"🔄 Starting Behavior Tree Loop (Tick Period: {tickPeriodMs}ms)");
            Console.WriteLine("   Press Ctrl+C to stop");
            Console.WriteLine();
            
            try
            {
                while (keepRunning && !ctrlCPressed)
                {
                    tickCount++;
                    var tickStartTime = DateTime.UtcNow;
                    
                    // Tick: Führe Tree EINMAL aus (wie BehaviorTree.CPP)
                    result = await rootNode.Execute();
                    
                    var tickDuration = (DateTime.UtcNow - tickStartTime).TotalMilliseconds;
                    
                    if (result == NodeStatus.Success)
                    {
                        Console.WriteLine();
                        Console.WriteLine("────────────────────────────────────────────────────────────────");
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("✅ BEHAVIOR TREE ERFOLGREICH ABGESCHLOSSEN");
                        Console.ResetColor();
                        Console.WriteLine($"   Total Ticks: {tickCount}");
                        Console.WriteLine($"   Total Time: {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                        break;
                    }
                    else if (result == NodeStatus.Failure)
                    {
                        Console.WriteLine();
                        Console.WriteLine("────────────────────────────────────────────────────────────────");
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("❌ BEHAVIOR TREE FEHLGESCHLAGEN");
                        Console.ResetColor();
                        Console.WriteLine($"   Total Ticks: {tickCount}");
                        break;
                    }
                    else if (result == NodeStatus.Running)
                    {
                        // Status-Update alle 50 Ticks (~5 Sekunden bei 100ms Tick)
                        if (tickCount % 50 == 0)
                        {
                            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                            Console.WriteLine($"⏳ Tree Running... (Tick #{tickCount}, {elapsed:F1}s elapsed, last tick: {tickDuration:F1}ms)");
                        }
                        
                        // Warnung bei langsamen Ticks (nur alle 100 Ticks)
                        if (tickDuration > tickPeriodMs * 2 && tickCount % 100 == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"⚠️  Slow Tick detected: {tickDuration:F1}ms (target: {tickPeriodMs}ms)");
                            Console.ResetColor();
                        }
                        
                        // Sleep bis nächster Tick (wie BehaviorTree.CPP)
                        var sleepTime = Math.Max(0, tickPeriodMs - (int)tickDuration);
                        if (sleepTime > 0)
                        {
                            await Task.Delay(sleepTime);
                        }
                    }
                }
            }
            finally
            {
                // Unregister Ctrl+C Handler
                if (cancelHandler != null)
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
            
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────────────────────────────");
            Console.WriteLine();
            
            PrintContextState(context);
            
            Console.WriteLine();
            Console.WriteLine("🧹 Cleanup...");
            // Graceful shutdown: flush/coalesce pending MQTT publishes from StorageMqttNotifier
            await MAS_BT.Services.ShutdownHelper.ShutdownStorageNotifierAsync(context, logger);

            var server = context.Get<RemoteServer>("RemoteServer");
            if (server != null)
            {
                try
                {
                    server.Dispose();
                    Console.WriteLine("✓ RemoteServer disconnected");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing RemoteServer");
                }
            }
            
            Console.WriteLine("✓ Cleanup abgeschlossen");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ KRITISCHER FEHLER:");
            Console.WriteLine($"   {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Stack Trace:");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
        }
        finally
        {
            // MQTT Cleanup
            if (messagingClient != null)
            {
                try
                {
                    await messagingClient.DisconnectAsync();
                    messagingClient.Dispose();
                    Console.WriteLine("✓ MQTT Client disconnected");
                }
                catch { }
            }
        }
        
        Console.WriteLine();
        Console.WriteLine("👋 Test beendet");
    }
    
    private static Dictionary<string, object> LoadConfig()
    {
        return LoadConfig(null);
    }

    private static Dictionary<string, object> LoadConfig(string? configPath)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? "config.json" : configPath;

        if (!File.Exists(path))
        {
            Console.WriteLine($"⚠️  Config nicht gefunden: {path}, verwende Defaults");
            return new Dictionary<string, object>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            var result = new Dictionary<string, object>();
            if (config != null)
            {
                foreach (var kvp in config)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Fehler beim Laden von {path}: {ex.Message}");
            return new Dictionary<string, object>();
        }
    }

    private static string? GetInitializationTreeFromConfig(Dictionary<string, object> config)
    {
        if (config == null || config.Count == 0)
            return null;

        // Top-level InitializationTree
        if (config.TryGetValue("InitializationTree", out var top))
        {
            if (top is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return top?.ToString();
        }

        // Nested common keys
        if (config.TryGetValue("ProductAgent", out var productObj) && productObj is JsonElement prodElem && prodElem.ValueKind == JsonValueKind.Object)
        {
            if (prodElem.TryGetProperty("InitializationTree", out var initElem) && initElem.ValueKind == JsonValueKind.String)
                return initElem.GetString();
        }

        // Try Agent.* keys - e.g., some configs may provide a preferred Tree entry
        if (config.TryGetValue("Agent", out var agentObj) && agentObj is JsonElement agentElem && agentElem.ValueKind == JsonValueKind.Object)
        {
            if (agentElem.TryGetProperty("InitializationTree", out var initAgent) && initAgent.ValueKind == JsonValueKind.String)
                return initAgent.GetString();
        }

        return null;
    }
    
    private static string? ResolveBehaviorTreePath(string[] args)
    {
        if (args == null || args.Length == 0)
            return null;

        foreach (var rawArg in args)
        {
            if (string.IsNullOrWhiteSpace(rawArg) || rawArg == "--")
                continue;

            foreach (var candidate in BuildPathCandidates(rawArg))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var normalized = candidate.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
                if (File.Exists(normalized))
                    return normalized;

                if (!Path.IsPathRooted(normalized))
                {
                    var relativeToTrees = Path.Combine("Trees", normalized.TrimStart(Path.DirectorySeparatorChar));
                    if (File.Exists(relativeToTrees))
                        return relativeToTrees;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildPathCandidates(string rawArg)
    {
        yield return rawArg;

        if (rawArg.StartsWith("--", StringComparison.Ordinal))
        {
            var trimmed = rawArg.TrimStart('-');
            yield return trimmed;

            if (trimmed.StartsWith("/", StringComparison.Ordinal) || trimmed.StartsWith("\\", StringComparison.Ordinal))
            {
                yield return trimmed.TrimStart('/', '\\');
            }
        }
    }

    private static string GetConfigValue(Dictionary<string, object> config, string path, string defaultValue)
    {
        var parts = path.Split('.');
        object? current = config;
        
        foreach (var part in parts)
        {
            if (current is Dictionary<string, object> dict && dict.TryGetValue(part, out var value))
            {
                current = value;
            }
            else if (current is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(part, out var prop))
                {
                    current = prop;
                }
                else
                {
                    return defaultValue;
                }
            }
            else
            {
                return defaultValue;
            }
        }
        
        if (current is JsonElement jsonElem)
        {
            return jsonElem.GetString() ?? defaultValue;
        }
        
        return current?.ToString() ?? defaultValue;
    }
    
    private static int GetConfigInt(Dictionary<string, object> config, string path, int defaultValue)
    {
        var parts = path.Split('.');
        object? current = config;
        
        foreach (var part in parts)
        {
            if (current is Dictionary<string, object> dict && dict.TryGetValue(part, out var value))
            {
                current = value;
            }
            else if (current is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(part, out var prop))
                {
                    current = prop;
                }
                else
                {
                    return defaultValue;
                }
            }
            else
            {
                return defaultValue;
            }
        }
        
        if (current is JsonElement jsonElem)
        {
            if (jsonElem.ValueKind == JsonValueKind.Number)
            {
                return jsonElem.GetInt32();
            }
            else if (jsonElem.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(jsonElem.GetString(), out var intValue) ? intValue : defaultValue;
            }
        }
        
        if (current is int intVal)
        {
            return intVal;
        }
        
        return int.TryParse(current?.ToString() ?? "", out var parsed) ? parsed : defaultValue;
    }
    
    private static void PrintContextState(BTContext context)
    {
        Console.WriteLine("📊 CONTEXT STATE:");
        Console.WriteLine();
        
        var data = new Dictionary<string, object?>
        {
            ["connected"] = context.Get<bool>("connected"),
            ["locked"] = context.Get<bool>("locked"),
            ["coupled"] = context.Get<bool>("coupled"),
            ["started"] = context.Get<bool>("started"),
            ["sent"] = context.Get<bool>("sent"),
            ["moduleEndpoint"] = context.Get<string>("moduleEndpoint"),
            ["lastExecutedSkill"] = context.Get<string>("lastExecutedSkill"),
            ["CoupledModules"] = context.Get<List<string>>("CoupledModules")
        };
        
        foreach (var kvp in data)
        {
            if (kvp.Value != null)
            {
                Console.WriteLine($"   • {kvp.Key}: {FormatValue(kvp.Value)}");
            }
        }
        
        Console.WriteLine();
    }
    
    private static string FormatValue(object value)
    {
        if (value is bool b)
            return b ? "✓" : "✗";
        
        if (value is List<string> list)
            return $"[{string.Join(", ", list)}]";
        
        return value.ToString() ?? "(null)";
    }
}

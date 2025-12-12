using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Serialization;
using MAS_BT.Services;
using System.Text.Json;
using System.Linq;
using System.IO;
using UAClient.Client;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;

namespace MAS_BT.Examples;

public class ModuleInitializationTestRunner
{
    public static async Task Run(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════╗");
        Console.WriteLine("║    MAS-BT: Agent Spwaner           ║");
        Console.WriteLine("╚════════════════════════════════════╝");
        Console.WriteLine();
        
        var (filteredArgs, spawnSubHolonsInTerminal) = ParseFlags(args);

        // Lade Config: akzeptiere direkten Pfad oder Konfig-Namen (z.B. "P17")
        var providedConfigPath = ResolveConfigPath(filteredArgs);
        if (string.IsNullOrWhiteSpace(providedConfigPath) || !File.Exists(providedConfigPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ Config nicht gefunden. Bitte explizit angeben (Pfad oder Name unter configs/...).");
            Console.ResetColor();
            return;
        }

        var config = LoadConfig(providedConfigPath);

        if (TryGetAgentList(config, out var agentList) && agentList.Count > 0)
        {
            await SpawnAgentsAsync(providedConfigPath, agentList, spawnSubHolonsInTerminal);
            return;
        }
        var opcuaEndpoint = GetConfigValue(config, "OPCUA.Endpoint", "opc.tcp://192.168.178.30:4849");
        var opcuaUsername = GetConfigValue(config, "OPCUA.Username", "orchestrator");
        var opcuaPassword = GetConfigValue(config, "OPCUA.Password", "orchestrator");
        var agentId = GetConfigValue(config, "Agent.AgentId", "TestAgent");
        var agentRole = GetConfigValue(config, "Agent.Role", "ResourceHolon");
        var moduleId = GetConfigValue(config, "Agent.ModuleId", null)
                       ?? GetConfigValue(config, "Agent.ModuleName", null)
                       ?? agentId;
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
        var publishLogs = GetConfigBool(config, "MQTT.PublishLogs", true);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            // Standard-Log-Level auf Information setzen - Debug/Trace sind standardmäßig aus
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new MqttLoggerProvider(
                publishLogs ? messagingClient : null,
                () => context.AgentId,
                () => context.AgentRole,
                publishLogs));
        });
        
        var logger = loggerFactory.CreateLogger<ModuleInitializationTestRunner>();
        
        // BTContext Logger erst nach Factory-Erzeugung setzen
        context = new BTContext(loggerFactory.CreateLogger<BTContext>())
        {
            AgentId = agentId,
            AgentRole = agentRole
        };

        context.Set("config.Path", Path.GetFullPath(providedConfigPath));
        
        context.Set("config.OPCUA.Endpoint", opcuaEndpoint);
        context.Set("config.OPCUA.Username", opcuaUsername);
        context.Set("config.OPCUA.Password", opcuaPassword);
        context.Set("config.Agent.ModuleName", moduleId);
        // Expose AgentId and Role in config.* keys so placeholder resolution works
        context.Set("config.Agent.AgentId", agentId);
        context.Set("config.Agent.Role", agentRole);
        context.Set("config.MQTT.Broker", mqttBroker);
        context.Set("config.MQTT.Port", mqttPort);
        context.Set("SpawnSubHolonsInTerminal", spawnSubHolonsInTerminal);
        // Alias für Legacy-Nodes: einige Nodes lesen direkt 'ModuleId' aus dem Context
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
            // Bestimme Behavior-Tree-Pfad ausschließlich aus Config oder CLI-Angabe
            string? btFilePath = null;

            btFilePath = GetInitializationTreeFromConfig(config);

            if (string.IsNullOrWhiteSpace(btFilePath))
            {
                btFilePath = ResolveBehaviorTreePath(filteredArgs);
            }

            if (string.IsNullOrWhiteSpace(btFilePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("❌ Kein Behavior Tree angegeben (InitializationTree in Config oder .bt.xml als Argument).");
                Console.ResetColor();
                return;
            }
            
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
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            throw new FileNotFoundException("Config file is required", configPath ?? string.Empty);
        }

        try
        {
            var json = File.ReadAllText(configPath);
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
            Console.WriteLine($"⚠️  Fehler beim Laden von {configPath}: {ex.Message}");
            throw;
        }
    }

    private static (string[] filteredArgs, bool spawnSubHolonsInTerminal) ParseFlags(string[] args)
    {
        if (args == null || args.Length == 0) return (Array.Empty<string>(), false);

        var list = new List<string>();
        var spawnFlag = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--spawn-terminal", StringComparison.OrdinalIgnoreCase))
            {
                spawnFlag = true;
                continue;
            }

            list.Add(arg);
        }

        return (list.ToArray(), spawnFlag);
    }

    private static string? ResolveConfigPath(string[]? args)
    {
        if (args != null)
        {
            // 1) direct existing file argument
            var direct = args
                .Select(a => a?.Trim())
                .FirstOrDefault(a => !string.IsNullOrEmpty(a) && (a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || a.Contains("configs")) && File.Exists(a));
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            // 2) name argument like "P17" or "P17.json" → try well-known locations
            var nameArg = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            if (!string.IsNullOrWhiteSpace(nameArg))
            {
                // If the argument already looks like a path (contains directory separators), resolve it directly first.
                if (nameArg.Contains(Path.DirectorySeparatorChar) || nameArg.Contains(Path.AltDirectorySeparatorChar))
                {
                    var pathCandidate = Path.GetFullPath(nameArg);
                    if (File.Exists(pathCandidate))
                        return pathCandidate;
                }

                var baseName = nameArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameArg : nameArg + ".json";
                var searchName = Path.GetFileName(baseName); // avoid passing directory segments into search patterns

                var candidates = new List<string>
                {
                    Path.Combine("configs", searchName),
                    Path.Combine("configs", "specific_configs", "Module_configs", searchName),
                    Path.Combine("configs", "generic_configs", searchName)
                };

                // search recursively under configs for the basename to catch other folders
                if (Directory.Exists("configs"))
                {
                    var found = Directory.GetFiles("configs", searchName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        candidates.Insert(0, found);
                    }
                }

                foreach (var c in candidates)
                {
                    var full = Path.GetFullPath(c);
                    if (File.Exists(full))
                        return full;
                }
            }
        }

        // fallback to default
        return null;
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

            // Ignore obvious config files so the first arg (config path) is not mistaken as BT file
            var lower = rawArg.Trim().ToLowerInvariant();
            if (lower.EndsWith(".json") || lower.Contains("config"))
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

    private static async Task SpawnAgentsAsync(string aggregatorConfigPath, IReadOnlyList<string> agentEntries, bool launchInTerminal)
    {
        Console.WriteLine();
        Console.WriteLine($"👥 Agents list detected ({agentEntries.Count}) – spawning each entry{(launchInTerminal ? " in its own terminal" : "")}.");

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(aggregatorConfigPath)) ?? Directory.GetCurrentDirectory();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole();
        });
        var launcherLogger = loggerFactory.CreateLogger("MultiAgentSpawner");
        var launcher = new ProcessSubHolonLauncher(launcherLogger, launchInTerminal);

        var launchTasks = new List<Task>();
        foreach (var agentEntryRaw in agentEntries)
        {
            if (string.IsNullOrWhiteSpace(agentEntryRaw))
                continue;

            var trimmedEntry = agentEntryRaw.Trim();
            var resolvedConfigPath = ResolveAgentConfigPath(trimmedEntry, baseDir);
            if (string.IsNullOrWhiteSpace(resolvedConfigPath))
            {
                launcherLogger.LogWarning("No config found for agent entry {Entry}; skipping.", trimmedEntry);
                continue;
            }

            Dictionary<string, object> agentConfig;
            try
            {
                agentConfig = LoadConfig(resolvedConfigPath);
            }
            catch (Exception ex)
            {
                launcherLogger.LogWarning(ex, "Failed to parse config for {Agent} ({Config}); skipping.", trimmedEntry, resolvedConfigPath);
                continue;
            }

            var treePath = GetInitializationTreeFromConfig(agentConfig);
            var agentId = GetConfigValue(agentConfig, "Agent.AgentId", trimmedEntry);
            var moduleId = GetConfigValue(agentConfig, "Agent.ModuleId", null)
                           ?? GetConfigValue(agentConfig, "Agent.ModuleName", agentId)
                           ?? agentId;

            var spec = new SubHolonLaunchSpec(
                treePath,
                resolvedConfigPath,
                moduleId,
                agentId);

            launcherLogger.LogInformation("Launching agent {AgentId} using {Config}.", agentId, resolvedConfigPath);
            launchTasks.Add(launcher.LaunchAsync(spec));
        }

        if (launchTasks.Count == 0)
        {
            launcherLogger.LogWarning("Kein Agent wurde gestartet (configs fehlen oder fehlerhaft).");
            return;
        }

        await Task.WhenAll(launchTasks);
        Console.WriteLine("👥 Alle angeforderten Agenten wurden gestartet.");
    }

    private static bool TryGetAgentList(Dictionary<string, object> config, out IReadOnlyList<string> agents)
    {
        agents = Array.Empty<string>();
        if (config == null || !config.TryGetValue("Agents", out var value) || value == null)
            return false;

        var list = new List<string>();

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in element.EnumerateArray())
            {
                var entryValue = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString();
                if (!string.IsNullOrWhiteSpace(entryValue))
                    list.Add(entryValue!);
            }
        }
        else if (value is IEnumerable<object> enumerable)
        {
            foreach (var entry in enumerable)
            {
                var entryValue = entry?.ToString();
                if (!string.IsNullOrWhiteSpace(entryValue))
                    list.Add(entryValue!);
            }
        }
        else if (value is string asString)
        {
            if (!string.IsNullOrWhiteSpace(asString))
                list.Add(asString.Trim());
        }

        if (list.Count == 0)
            return false;

        agents = list;
        return true;
    }

    private static string? ResolveAgentConfigPath(string agentEntry, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(agentEntry))
            return null;

        var candidates = new List<string> { agentEntry };
        if (!agentEntry.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add($"{agentEntry}.json");
        }

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
                continue;
            }

            var relative = Path.Combine(baseDir, candidate);
            if (File.Exists(relative))
                return Path.GetFullPath(relative);
        }

        var resolved = ResolveConfigPath(new[] { agentEntry });
        if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
        {
            return Path.GetFullPath(resolved);
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

    private static bool GetConfigBool(Dictionary<string, object> config, string path, bool defaultValue)
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
            if (jsonElem.ValueKind == JsonValueKind.True) return true;
            if (jsonElem.ValueKind == JsonValueKind.False) return false;
            if (jsonElem.ValueKind == JsonValueKind.String)
                return bool.TryParse(jsonElem.GetString(), out var b) ? b : defaultValue;
        }

        if (current is bool bVal)
        {
            return bVal;
        }

        if (current != null && bool.TryParse(current.ToString(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
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

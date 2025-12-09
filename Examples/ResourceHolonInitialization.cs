using MAS_BT.Core;
using MAS_BT.Nodes.Configuration;
using MAS_BT.Nodes.Messaging;
using MAS_BT.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MAS_BT.Examples;

/// <summary>
/// Beispiel: Initialisierung eines Resource Holon mit allen Configuration Nodes
/// </summary>
public class ResourceHolonInitialization
{
    public static async Task Run(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║    MAS-BT: Resource Holon Initialization Example             ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        // Setup Logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        
        var logger = loggerFactory.CreateLogger<ResourceHolonInitialization>();
        
        // Erstelle Behavior Tree Context
        var context = new BTContext
        {
            AgentId = "ResourceHolon_RH2",
            AgentRole = "ResourceHolon"
        };

        context.Set("SkillRequestQueue", new SkillRequestQueue());
        // Precondition retry configuration: default to 10 retries and 5 minutes start timeout
        context.Set("MaxPreconditionRetries", 10);
        context.Set("PreconditionBackoffStartMs", 5 * 60 * 1000); // 5 minutes in ms
        
        Console.WriteLine($"🤖 Agent ID: {context.AgentId}");
        Console.WriteLine($"🏷️  Agent Role: {context.AgentRole}");
        Console.WriteLine();
        Console.WriteLine("────────────────────────────────────────────────────────────────");
        Console.WriteLine();
        
        try
        {
            // Phase 1: Verbinde zu MQTT Broker
            Console.WriteLine("📡 Phase 1: Connecting to MQTT Broker...");
            Console.WriteLine();
            
            var connectToBrokerNode = new ConnectToMessagingBrokerNode
            {
                BrokerHost = "localhost",
                BrokerPort = 1883,
                DefaultTopic = "factory/agents/messages",
                TimeoutMs = 10000
            };
            connectToBrokerNode.Initialize(context, loggerFactory.CreateLogger("ConnectToMessagingBroker"));
            
            var result = await connectToBrokerNode.Execute();
            if (result != NodeStatus.Success)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("✗ MQTT Verbindung fehlgeschlagen");
                Console.ResetColor();
                return;
            }
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ MQTT Broker verbunden");
            Console.ResetColor();
            Console.WriteLine();
            
            // Phase 2: Lade AAS Shell
            Console.WriteLine("📦 Phase 2: Loading AAS Shell...");
            Console.WriteLine();
            
            context.Set("AasEndpoint", "http://localhost:4001");
            
            var readShellNode = new ReadShellNode
            {
                AgentId = context.AgentId
            };
            readShellNode.Initialize(context, loggerFactory.CreateLogger("ReadShell"));
            
            result = await readShellNode.Execute();
            if (result == NodeStatus.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ AAS Shell geladen");
                Console.ResetColor();
            }
            Console.WriteLine();
            
            // Phase 3: Lade Capability Description
            Console.WriteLine("🎯 Phase 3: Loading Capability Description...");
            Console.WriteLine();
            
            var readCapabilityNode = new ReadCapabilityDescriptionNode
            {
                AgentId = context.AgentId
            };
            readCapabilityNode.Initialize(context, loggerFactory.CreateLogger("ReadCapabilityDescription"));
            
            result = await readCapabilityNode.Execute();
            if (result == NodeStatus.Success)
            {
                var capabilities = context.Get<object>($"CapabilityDescription_{context.AgentId}");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Capabilities: {System.Text.Json.JsonSerializer.Serialize(capabilities)}");
                Console.ResetColor();
            }
            Console.WriteLine();
            
            // Phase 4: Lade Skills
            Console.WriteLine("⚙️  Phase 4: Loading Skills...");
            Console.WriteLine();
            
            var readSkillsNode = new ReadSkillsNode
            {
                AgentId = context.AgentId
            };
            readSkillsNode.Initialize(context, loggerFactory.CreateLogger("ReadSkills"));
            
            result = await readSkillsNode.Execute();
            if (result == NodeStatus.Success)
            {
                var skills = context.Get<object>($"Skills_{context.AgentId}");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Skills: {System.Text.Json.JsonSerializer.Serialize(skills)}");
                Console.ResetColor();
            }
            Console.WriteLine();
            
            // Phase 5: Lade Machine Schedule
            Console.WriteLine("📅 Phase 5: Loading Machine Schedule...");
            Console.WriteLine();
            
            var readScheduleNode = new ReadMachineScheduleNode
            {
                AgentId = context.AgentId
            };
            readScheduleNode.Initialize(context, loggerFactory.CreateLogger("ReadMachineSchedule"));
            
            result = await readScheduleNode.Execute();
            if (result == NodeStatus.Success)
            {
                var schedule = context.Get<object>($"MachineSchedule_{context.AgentId}");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Schedule: {System.Text.Json.JsonSerializer.Serialize(schedule)}");
                Console.ResetColor();
            }
            Console.WriteLine();
            
            // Phase 6: Lade Nameplate
            Console.WriteLine("🏷️  Phase 6: Loading Nameplate...");
            Console.WriteLine();
            
            var readNameplateNode = new ReadNameplateNode
            {
                AgentId = context.AgentId
            };
            readNameplateNode.Initialize(context, loggerFactory.CreateLogger("ReadNameplate"));
            
            result = await readNameplateNode.Execute();
            if (result == NodeStatus.Success)
            {
                var nameplate = context.Get<object>($"Nameplate_{context.AgentId}");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Nameplate: {System.Text.Json.JsonSerializer.Serialize(nameplate)}");
                Console.ResetColor();
            }
            Console.WriteLine();
            
            // Phase 7: Couple mit Nachbarmodul
            Console.WriteLine("🔗 Phase 7: Coupling with neighbor module...");
            Console.WriteLine();
            
            var coupleModuleNode = new CoupleModuleNode
            {
                ModuleId = "ResourceHolon_RH3"
            };
            coupleModuleNode.Initialize(context, loggerFactory.CreateLogger("CoupleModule"));
            
            result = await coupleModuleNode.Execute();
            if (result == NodeStatus.Success)
            {
                var coupledModules = context.Get<List<string>>("CoupledModules");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Coupled Modules: {string.Join(", ", coupledModules ?? new List<string>())}");
                Console.ResetColor();
            }
            Console.WriteLine();
            
            // Zusammenfassung
            Console.WriteLine("────────────────────────────────────────────────────────────────");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("✅ Resource Holon erfolgreich initialisiert!");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("📊 Context State:");
            Console.WriteLine($"   • MessagingClient: {(context.Has("MessagingClient") ? "✓" : "✗")}");
            Console.WriteLine($"   • AAS Shell: {(context.Has($"Shell_{context.AgentId}") ? "✓" : "✗")}");
            Console.WriteLine($"   • MessagingClient: {(context.Has("MessagingClient") ? "✓" : "✗")}");
            Console.WriteLine($"   • AAS Shell: {(context.Has($"Shell_{context.AgentId}") ? "✓" : "✗")}");
            Console.WriteLine($"   • Capabilities: {(context.Has($"CapabilityDescription_{context.AgentId}") ? "✓" : "✗")}");
            Console.WriteLine($"   • Skills: {(context.Has($"Skills_{context.AgentId}") ? "✓" : "✗")}");
            Console.WriteLine($"   • Schedule: {(context.Has($"MachineSchedule_{context.AgentId}") ? "✓" : "✗")}");
            Console.WriteLine($"   • Nameplate: {(context.Has($"Nameplate_{context.AgentId}") ? "✓" : "✗")}");
            Console.WriteLine($"   • Coupled Modules: {context.Get<List<string>>("CoupledModules")?.Count ?? 0}");
            Console.WriteLine();
            
            Console.WriteLine("💡 Next Steps:");
            Console.WriteLine("   1. Start monitoring loop (CheckReadyState, CheckLockedState)");
            Console.WriteLine("   2. Subscribe to incoming messages (WaitForMessage)");
            Console.WriteLine("   3. Begin bidding process (ExecuteCapabilityMatchmaking)");
            Console.WriteLine();
            
            // Cleanup
            Console.WriteLine("🧹 Cleanup...");
            // Graceful shutdown: flush pending Inventory MQTT publishes if notifier is present
            await MAS_BT.Services.ShutdownHelper.ShutdownStorageNotifierAsync(context, logger);

            await connectToBrokerNode.OnAbort();
            Console.WriteLine("✓ Verbindungen geschlossen");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Fehler: {ex.Message}");
            Console.WriteLine($"   {ex.StackTrace}");
            Console.ResetColor();
        }
        
        Console.WriteLine();
        Console.WriteLine("👋 Beispiel beendet");
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using Xunit;

namespace MAS_BT.Tests;

/// <summary>
/// Integration-Tests für Neo4j-Agent-Synchronisation.
/// Testet das Syncen von Agents (RegisterMessage) und Inventory (InventoryMessage) in Neo4j.
/// </summary>
public class Neo4jAgentSyncTests : IAsyncDisposable
{
    private readonly IDriver? _driver;
    private readonly string _database = "neo4j";
    private readonly bool _skipTests;

    // Test-Namespace und IDs
    private const string TestNamespace = "test_agent_sync";
    private const string TestModuleId = "P102";
    private const string TestPlanningId = "P102_Planning";
    private const string TestExecutionId = "P102_Execution";
    private const string TestDispatchingId = "DispatchingAgent";

    public Neo4jAgentSyncTests()
    {
        // Neo4j-Verbindung nur wenn verfügbar (aus Umgebungsvariablen oder Config)
        var uri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://192.168.178.30:7687";
        var user = Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "testtest";

        try
        {
            _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
            _skipTests = false;
        }
        catch
        {
            _skipTests = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_driver != null)
        {
            // Cleanup: Löschen aller Test-Nodes
            try
            {
                await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
                await session.RunAsync(
                    "MATCH (a:Agent) WHERE a.namespace = $ns DETACH DELETE a",
                    new { ns = TestNamespace });
                await session.RunAsync(
                    "MATCH (s:Storage) WHERE s.moduleId = $id DETACH DELETE s",
                    new { id = TestModuleId });

                // Slot-Details können nach DETACH DELETE(Storage) als verwaiste Nodes übrig bleiben.
                // Deshalb best-effort auch Slots anhand des Prefix löschen.
                await session.RunAsync(
                    "MATCH (sl:Slot) WHERE sl.slotId STARTS WITH $prefix DETACH DELETE sl",
                    new { prefix = $"{TestModuleId}_" });

                await session.RunAsync(
                    "MATCH (s:Storage) WHERE s.storageId STARTS WITH $prefix DETACH DELETE s",
                    new { prefix = $"{TestModuleId}_" });
            }
            catch
            {
                // best-effort cleanup
            }

            await _driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task SyncAgentToNeo4j_CreatesModuleHolonNode()
    {
        if (_skipTests)
        {
            return; // Skip if Neo4j not available
        }

        // Arrange
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = TestModuleId,
            AgentRole = "ModuleHolon"
        };
        context.Set("Neo4jDriver", _driver);
        context.Set("config.Neo4j.Database", _database);
        context.Set("config.Namespace", TestNamespace);
        context.Set("config.Agent.ParentAgent", TestDispatchingId);

        var registerMsg = CreateRegisterMessage(
            TestModuleId,
            "ModuleHolon",
            subagents: new List<string> { TestPlanningId, TestExecutionId },
            capabilities: new List<string> { "Assemble" });

        context.Set("LastReceivedMessage", registerMsg);

        var node = new SyncAgentToNeo4jNode { Context = context };
        node.SetLogger(NullLogger<SyncAgentToNeo4jNode>.Instance);

        // Act
        var status = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, status);

        // Verify in Neo4j
        await using var session = _driver!.AsyncSession(o => o.WithDatabase(_database));
        var cursor = await session.RunAsync(
            @"MATCH (a:Agent:ModuleHolon {agentId: $agentId})
              RETURN a.agentId AS agentId, a.agentType AS agentType, a.isActive AS isActive, a.capabilities AS capabilities",
            new { agentId = TestModuleId });

        var record = await cursor.SingleAsync();
        Assert.Equal(TestModuleId, record["agentId"].As<string>());
        Assert.Equal("ModuleHolon", record["agentType"].As<string>());
        Assert.True(record["isActive"].As<bool>());
        Assert.Contains("Assemble", record["capabilities"].As<List<string>>());
    }

    [Fact]
    public async Task SyncAgentToNeo4j_CreatesSubagentRelationship()
    {
        if (_skipTests)
        {
            return;
        }

        // Arrange: Zuerst ModuleHolon syncen
        await SyncAgentToNeo4j_CreatesModuleHolonNode();

        // Jetzt PlanningHolon syncen (als Subagent von P102)
        var planningContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = TestPlanningId,
            AgentRole = "PlanningHolon"
        };
        planningContext.Set("Neo4jDriver", _driver);
        planningContext.Set("config.Neo4j.Database", _database);
        planningContext.Set("config.Namespace", TestNamespace);
        planningContext.Set("config.Agent.ParentAgent", TestModuleId);
        planningContext.Set("config.Agent.ModuleId", TestModuleId);

        var planningRegMsg = CreateRegisterMessage(
            TestPlanningId,
            "PlanningHolon",
            subagents: new List<string>(),
            capabilities: new List<string>());

        planningContext.Set("LastReceivedMessage", planningRegMsg);

        var node = new SyncAgentToNeo4jNode { Context = planningContext };
        node.SetLogger(NullLogger<SyncAgentToNeo4jNode>.Instance);

        // Act
        var status = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, status);

        // Verify relationship
        await using var session = _driver!.AsyncSession(o => o.WithDatabase(_database));
        var cursor = await session.RunAsync(
            @"MATCH (sub:Agent {agentId: $subId})-[r:IS_SUBAGENT_OF]->(parent:Agent {agentId: $parentId})
              RETURN sub.agentId AS subId, parent.agentId AS parentId",
            new { subId = TestPlanningId, parentId = TestModuleId });

        var record = await cursor.SingleAsync();
        Assert.Equal(TestPlanningId, record["subId"].As<string>());
        Assert.Equal(TestModuleId, record["parentId"].As<string>());
    }

    [Fact]
    public async Task SyncAgentToNeo4j_CreatesManagesAssetRelationship()
    {
        if (_skipTests)
        {
            return;
        }

        // Arrange: Zuerst Asset-Node erstellen (simuliert AAS-Import)
        await using (var setupSession = _driver!.AsyncSession(o => o.WithDatabase(_database)))
        {
            await setupSession.RunAsync(
                @"MERGE (asset:Asset {shell_id: $shellId})
                  SET asset.name = $name
                  MERGE (pos:Position {X: '40', Y: '20'})
                  MERGE (asset)-[:HAS_POSITION]->(pos)",
                new { shellId = TestModuleId, name = "AssemblyStation" });
        }

        // Jetzt ModuleHolon syncen
        await SyncAgentToNeo4j_CreatesModuleHolonNode();

        // Verify MANAGES_ASSET relationship
        await using var session = _driver!.AsyncSession(o => o.WithDatabase(_database));
        var cursor = await session.RunAsync(
            @"MATCH (agent:Agent:ModuleHolon {agentId: $agentId})-[r:MANAGES_ASSET]->(asset:Asset {shell_id: $shellId})
              RETURN agent.agentId AS agentId, asset.shell_id AS shellId",
            new { agentId = TestModuleId, shellId = TestModuleId });

        var record = await cursor.SingleAsync();
        Assert.Equal(TestModuleId, record["agentId"].As<string>());
        Assert.Equal(TestModuleId, record["shellId"].As<string>());
    }

    [Fact]
    public async Task SyncInventoryToNeo4j_CreatesStorageNodes()
    {
        if (_skipTests)
        {
            return;
        }

        // Arrange: Zuerst ModuleHolon syncen
        await SyncAgentToNeo4j_CreatesModuleHolonNode();

        // Sicherstellen, dass keine Alt-Daten (Storage/Slot) für P102 die Assertions verfälschen
        await CleanupInventoryForModuleAsync(TestModuleId);

        // Inventory Message erstellen
        var storageUnits = new List<StorageUnit>
        {
            new StorageUnit
            {
                Name = "InputConveyor",
                Slots = new List<Slot>
                {
                    new Slot { Index = 0, Content = new SlotContent { IsSlotEmpty = true } },
                    new Slot { Index = 1, Content = new SlotContent { IsSlotEmpty = false, ProductID = "WP_001" } },
                    new Slot { Index = 2, Content = new SlotContent { IsSlotEmpty = true } }
                }
            },
            new StorageUnit
            {
                Name = "OutputConveyor",
                Slots = new List<Slot>
                {
                    new Slot { Index = 0, Content = new SlotContent { IsSlotEmpty = true } },
                    new Slot { Index = 1, Content = new SlotContent { IsSlotEmpty = true } }
                }
            }
        };

        var inventoryMsg = CreateInventoryMessage(TestModuleId, "ModuleHolon", storageUnits);

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = TestModuleId,
            AgentRole = "ModuleHolon"
        };
        context.Set("Neo4jDriver", _driver);
        context.Set("config.Neo4j.Database", _database);
        context.Set("LastReceivedMessage", inventoryMsg);

        var node = new SyncInventoryToNeo4jNode { Context = context };
        node.SetLogger(NullLogger<SyncInventoryToNeo4jNode>.Instance);

        // Act
        var status = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, status);

        // Verify Storage nodes
        await using var session = _driver!.AsyncSession(o => o.WithDatabase(_database));
        var cursor = await session.RunAsync(
            @"MATCH (agent:Agent {agentId: $moduleId})-[:HAS_STORAGE]->(s:Storage)
                            WHERE s.storageId STARTS WITH $prefix
              RETURN s.storageId AS storageId, s.name AS name, s.freeSlots AS freeSlots, s.occupiedSlots AS occupiedSlots
                            ORDER BY s.name",
                        new { moduleId = TestModuleId, prefix = $"{TestModuleId}_" });

        var records = await cursor.ToListAsync();
        Assert.Equal(2, records.Count);

        // InputConveyor: 2 free, 1 occupied
        var input = records[0];
        Assert.Equal($"{TestModuleId}_InputConveyor", input["storageId"].As<string>());
        Assert.Equal("InputConveyor", input["name"].As<string>());
        Assert.Equal(2, input["freeSlots"].As<int>());
        Assert.Equal(1, input["occupiedSlots"].As<int>());

        // OutputConveyor: 2 free, 0 occupied
        var output = records[1];
        Assert.Equal($"{TestModuleId}_OutputConveyor", output["storageId"].As<string>());
        Assert.Equal("OutputConveyor", output["name"].As<string>());
        Assert.Equal(2, output["freeSlots"].As<int>());
        Assert.Equal(0, output["occupiedSlots"].As<int>());

                // Verify Slot detail nodes under Storage
                var slotCursor = await session.RunAsync(
                        @"MATCH (:Agent {agentId: $moduleId})-[:HAS_STORAGE]->(:Storage {storageId: $storageId})-[hs:HAS_SLOT]->(sl:Slot)
                            RETURN sl.slotId AS slotId, sl.index AS idx, sl.isEmpty AS isEmpty, sl.productId AS productId, hs.index AS relIndex
                            ORDER BY sl.index",
                        new { moduleId = TestModuleId, storageId = $"{TestModuleId}_InputConveyor" });

                var slots = await slotCursor.ToListAsync();
                Assert.Equal(3, slots.Count);

                Assert.Equal($"{TestModuleId}_InputConveyor_Slot_0", slots[0]["slotId"].As<string>());
                Assert.Equal(0, slots[0]["idx"].As<int>());
                Assert.True(slots[0]["isEmpty"].As<bool>());
                Assert.Equal(string.Empty, slots[0]["productId"].As<string>());
                Assert.Equal(0, slots[0]["relIndex"].As<int>());

                Assert.Equal($"{TestModuleId}_InputConveyor_Slot_1", slots[1]["slotId"].As<string>());
                Assert.Equal(1, slots[1]["idx"].As<int>());
                Assert.False(slots[1]["isEmpty"].As<bool>());
                Assert.Equal("WP_001", slots[1]["productId"].As<string>());
                Assert.Equal(1, slots[1]["relIndex"].As<int>());

                Assert.Equal($"{TestModuleId}_InputConveyor_Slot_2", slots[2]["slotId"].As<string>());
                Assert.Equal(2, slots[2]["idx"].As<int>());
                Assert.True(slots[2]["isEmpty"].As<bool>());
                Assert.Equal(string.Empty, slots[2]["productId"].As<string>());
                Assert.Equal(2, slots[2]["relIndex"].As<int>());
    }

    [Fact]
    public async Task DeregisterAgentFromNeo4j_SetsIsActiveFalse()
    {
        if (_skipTests)
        {
            return;
        }

        // Arrange: Zuerst Agent syncen
        await SyncAgentToNeo4j_CreatesModuleHolonNode();

        var context = new BTContext(NullLogger<BTContext>.Instance);
        context.Set("Neo4jDriver", _driver);
        context.Set("config.Neo4j.Database", _database);
        context.Set("AgentId", TestModuleId);

        var node = new DeregisterAgentFromNeo4jNode
        {
            Context = context,
            DeleteNode = false
        };
        node.SetLogger(NullLogger<DeregisterAgentFromNeo4jNode>.Instance);

        // Act
        var status = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, status);

        // Verify isActive=false
        await using var session = _driver!.AsyncSession(o => o.WithDatabase(_database));
        var cursor = await session.RunAsync(
            "MATCH (a:Agent {agentId: $agentId}) RETURN a.isActive AS isActive",
            new { agentId = TestModuleId });

        var record = await cursor.SingleAsync();
        Assert.False(record["isActive"].As<bool>());
    }

    [Fact]
    public async Task P102_CompleteScenario_SyncsFullHierarchy()
    {
        if (_skipTests)
        {
            return;
        }

        // Alt-Daten entfernen, damit COUNT nicht verfälscht wird
        await CleanupInventoryForModuleAsync(TestModuleId);

        // Arrange: Asset vorbereiten
        await using (var setupSession = _driver!.AsyncSession(o => o.WithDatabase(_database)))
        {
            await setupSession.RunAsync(
                @"MERGE (asset:Asset {shell_id: $shellId})
                  MERGE (pos:Position {X: '40', Y: '20'})
                  MERGE (asset)-[:HAS_POSITION]->(pos)",
                new { shellId = TestModuleId });
        }

        // 1. DispatchingAgent registrieren
        var dispatchContext = CreateAgentContext(TestDispatchingId, "DispatchingAgent", parentAgent: null);
        var dispatchRegMsg = CreateRegisterMessage(TestDispatchingId, "DispatchingAgent",
            subagents: new List<string> { TestModuleId },
            capabilities: new List<string>());
        dispatchContext.Set("LastReceivedMessage", dispatchRegMsg);

        var syncDispatch = new SyncAgentToNeo4jNode { Context = dispatchContext };
        syncDispatch.SetLogger(NullLogger<SyncAgentToNeo4jNode>.Instance);
        Assert.Equal(NodeStatus.Success, await syncDispatch.Execute());

        // 2. P102 ModuleHolon registrieren
        var moduleContext = CreateAgentContext(TestModuleId, "ModuleHolon", parentAgent: TestDispatchingId);
        var moduleRegMsg = CreateRegisterMessage(TestModuleId, "ModuleHolon",
            subagents: new List<string> { TestPlanningId, TestExecutionId },
            capabilities: new List<string> { "Assemble" });
        moduleContext.Set("LastReceivedMessage", moduleRegMsg);

        var syncModule = new SyncAgentToNeo4jNode { Context = moduleContext };
        syncModule.SetLogger(NullLogger<SyncAgentToNeo4jNode>.Instance);
        Assert.Equal(NodeStatus.Success, await syncModule.Execute());

        // 3. P102_Planning registrieren
        var planningContext = CreateAgentContext(TestPlanningId, "PlanningHolon", parentAgent: TestModuleId);
        var planningRegMsg = CreateRegisterMessage(TestPlanningId, "PlanningHolon",
            subagents: new List<string>(),
            capabilities: new List<string>());
        planningContext.Set("LastReceivedMessage", planningRegMsg);
        planningContext.Set("config.Agent.ModuleId", TestModuleId);

        var syncPlanning = new SyncAgentToNeo4jNode { Context = planningContext };
        syncPlanning.SetLogger(NullLogger<SyncAgentToNeo4jNode>.Instance);
        Assert.Equal(NodeStatus.Success, await syncPlanning.Execute());

        // 4. P102_Execution registrieren
        var executionContext = CreateAgentContext(TestExecutionId, "ExecutionHolon", parentAgent: TestModuleId);
        var executionRegMsg = CreateRegisterMessage(TestExecutionId, "ExecutionHolon",
            subagents: new List<string>(),
            capabilities: new List<string>());
        executionContext.Set("LastReceivedMessage", executionRegMsg);
        executionContext.Set("config.Agent.ModuleId", TestModuleId);

        var syncExecution = new SyncAgentToNeo4jNode { Context = executionContext };
        syncExecution.SetLogger(NullLogger<SyncAgentToNeo4jNode>.Instance);
        Assert.Equal(NodeStatus.Success, await syncExecution.Execute());

        // 5. Inventory syncen
        var storageUnits = new List<StorageUnit>
        {
            new StorageUnit
            {
                Name = "InputConveyor",
                Slots = new List<Slot>
                {
                    new Slot { Index = 0, Content = new SlotContent { IsSlotEmpty = false, ProductID = "WP_001" } },
                    new Slot { Index = 1, Content = new SlotContent { IsSlotEmpty = true } }
                }
            }
        };
        var inventoryMsg = CreateInventoryMessage(TestModuleId, "ModuleHolon", storageUnits);
        moduleContext.Set("LastReceivedMessage", inventoryMsg);

        var syncInventory = new SyncInventoryToNeo4jNode { Context = moduleContext };
        syncInventory.SetLogger(NullLogger<SyncInventoryToNeo4jNode>.Instance);
        Assert.Equal(NodeStatus.Success, await syncInventory.Execute());

        // Verify complete graph structure
        await using var session = _driver!.AsyncSession(o => o.WithDatabase(_database));

        // Verify hierarchy
        var hierarchyQuery = @"
MATCH (dispatch:DispatchingAgent {agentId: $dispatchId})
OPTIONAL MATCH (dispatch)<-[:IS_SUBAGENT_OF]-(module:ModuleHolon {agentId: $moduleId})
OPTIONAL MATCH (module)<-[:IS_SUBAGENT_OF]-(planning:PlanningHolon {agentId: $planningId})
OPTIONAL MATCH (module)<-[:IS_SUBAGENT_OF]-(execution:ExecutionHolon {agentId: $executionId})
OPTIONAL MATCH (module)-[:MANAGES_ASSET]->(asset:Asset {shell_id: $moduleId})
OPTIONAL MATCH (module)-[:HAS_STORAGE]->(storage:Storage)
WHERE storage.storageId STARTS WITH $storagePrefix
RETURN 
    dispatch.agentId AS dispatchId,
    module.agentId AS moduleId,
    planning.agentId AS planningId,
    execution.agentId AS executionId,
    asset.shell_id AS assetId,
    count(DISTINCT storage) AS storageCount
";

        var cursor = await session.RunAsync(hierarchyQuery, new
        {
            dispatchId = TestDispatchingId,
            moduleId = TestModuleId,
            planningId = TestPlanningId,
            executionId = TestExecutionId,
            storagePrefix = $"{TestModuleId}_"
        });

        var result = await cursor.SingleAsync();
        Assert.Equal(TestDispatchingId, result["dispatchId"].As<string>());
        Assert.Equal(TestModuleId, result["moduleId"].As<string>());
        Assert.Equal(TestPlanningId, result["planningId"].As<string>());
        Assert.Equal(TestExecutionId, result["executionId"].As<string>());
        Assert.Equal(TestModuleId, result["assetId"].As<string>());
        Assert.Equal(1, result["storageCount"].As<int>());
    }

    private async Task CleanupInventoryForModuleAsync(string moduleId)
    {
        if (_driver == null)
        {
            return;
        }

        var prefix = $"{moduleId}_";
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        // Delete storages attached to the module agent (incl. HAS_SLOT rels)
        await session.RunAsync(
            @"MATCH (:Agent {agentId: $moduleId})-[:HAS_STORAGE]->(s:Storage)
              DETACH DELETE s",
            new { moduleId });

        // Delete any leftover storages/slots (e.g., if they got detached earlier)
        await session.RunAsync(
            "MATCH (s:Storage) WHERE s.storageId STARTS WITH $prefix DETACH DELETE s",
            new { prefix });

        await session.RunAsync(
            "MATCH (sl:Slot) WHERE sl.slotId STARTS WITH $prefix DETACH DELETE sl",
            new { prefix });
    }

    // Helper methods

    private BTContext CreateAgentContext(string agentId, string role, string? parentAgent)
    {
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = agentId,
            AgentRole = role
        };
        context.Set("Neo4jDriver", _driver);
        context.Set("config.Neo4j.Database", _database);
        context.Set("config.Namespace", TestNamespace);
        if (!string.IsNullOrWhiteSpace(parentAgent))
        {
            context.Set("config.Agent.ParentAgent", parentAgent);
        }
        return context;
    }

    private I40Message CreateRegisterMessage(string agentId, string role, List<string> subagents, List<string> capabilities)
    {
        var registerMsg = new RegisterMessage(agentId, subagents, capabilities);

        var builder = new I40MessageBuilder()
            .From(agentId, role)
            .To("Parent", "Agent")
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(registerMsg.ToSubmodelElementCollection());

        return builder.Build();
    }

    private I40Message CreateInventoryMessage(string moduleId, string role, List<StorageUnit> storageUnits)
    {
        var inventoryCollection = new InventoryMessage(storageUnits);

        var builder = new I40MessageBuilder()
            .From(moduleId, role)
            .To("Parent", "Agent")
            .WithType("inventoryUpdate")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(inventoryCollection);

        return builder.Build();
    }
}

# Neo4j Agent-Synchronisation: Konzept & Design

## 1. Analyse der vorhandenen Nachrichten

### 1.1 RegisterMessage
**Zweck:** Hierarchische Registrierung von Agenten beim übergeordneten Parent  
**Struktur:**
```csharp
class RegisterMessage {
    DateTime Timestamp;
    string AgentId;              // z.B. "P102", "P102_Planning", "DispatchingAgent_phuket"
    List<string> Subagents;      // Direkte Kinder (keine Sub-Subs)
    List<string> Capabilities;   // Namen der CapabilityContainer
}
```

**Hierarchie (Topics):**
- `{ns}/P102/Register` ← Sub-Holons (P102_Execution, P102_Planning)
- `{ns}/DispatchingAgent/Register` ← ModuleHolons (P102, P101, P100)
- `{ns}/Register` ← DispatchingAgent registriert sich beim Namespace

**Wichtig:**
- Capabilities werden nur als Namen übertragen (nicht die vollständigen Container)
- Sub-Holons (Planning/Execution) senden MessageType "subHolonRegister"
- Module/Dispatcher senden MessageType "registerMessage"

### 1.2 InventoryMessage
**Zweck:** Übertragung von Storage-Zuständen (Slots, Carrier, Produkte)  
**Struktur:**
```csharp
class InventoryMessage {
    List<StorageUnit> StorageUnits;
}

class StorageUnit {
    string Name;              // z.B. "InputConveyor", "OutputConveyor"
    List<Slot> Slots;
}

class Slot {
    int Index;
    SlotContent Content {
        string CarrierID;
        string CarrierType;
        string ProductType;
        string ProductID;
        bool IsSlotEmpty;
    }
}
```

**Aggregiert im DispatchingAgent:**
```csharp
class InventorySummary {
    int free;        // Summe aller freien Slots
    int occupied;    // Summe aller belegten Slots
}
```

### 1.3 DispatchingModuleInfo (interne Struktur)
**Zweck:** DispatchingAgent hält In-Memory-State aller registrierten Module  
**Struktur:**
```csharp
class DispatchingModuleInfo {
    string ModuleId;
    string? AasId;
    List<string> Capabilities;
    List<string> Neighbors;           // aus NeighborMessage
    int InventoryFree;
    int InventoryOccupied;
    DateTime LastRegistrationUtc;
    DateTime LastSeenUtc;
}
```

**Stale-Module-Pruning:**
- Timeout-basiert (Standard: 30s)
- Wird beim Register-Publish des DispatchingAgent durchgeführt
- Entfernt Module aus State, wenn LastSeenUtc > Timeout

---

## 2. Neo4j Graph-Schema Design

### 2.1 Node-Typen & Properties

#### Agent (Basis-Label für alle Agenten)
```cypher
(:Agent {
    agentId: string,           // Eindeutige ID (z.B. "P102", "P102_Planning")
    agentType: string,         // "ModuleHolon", "PlanningHolon", "ExecutionHolon", "DispatchingAgent", "ProductAgent"
    namespace: string,         // z.B. "phuket"
    lastRegistration: datetime,
    lastSeen: datetime,
    isActive: boolean          // false wenn stale/deregistered
})
```

**Spezialisierungen (zusätzliche Labels):**

**ModuleHolon:**
```cypher
(:Agent:ModuleHolon {
    agentId: "P102",
    aasId: string?,            // Optional: AAS-Shell-ID
    moduleName: string?,       // z.B. "AssemblyStation"
})
```

**PlanningHolon / ExecutionHolon:**
```cypher
(:Agent:PlanningHolon {
    agentId: "P102_Planning",
    moduleId: "P102"           // Referenz zum verwalteten Modul
})
```

**DispatchingAgent:**
```cypher
(:Agent:DispatchingAgent {
    agentId: "DispatchingAgent_phuket",
})
```

**ProductAgent:**
```cypher
(:Agent:ProductAgent {
    agentId: "Product_12345",
    productType: string?,
    productId: string?
})
```

#### Storage (für Inventory-Daten)
```cypher
(:Storage {
    storageId: string,         // Kombination: "{moduleId}_{storageName}"
    name: string,              // z.B. "InputConveyor"
    moduleId: string,          // Referenz zum Modul
    totalSlots: int,
    freeSlots: int,
    occupiedSlots: int,
    lastUpdated: datetime
})
```

#### Slot (optionale Detail-Nodes)
```cypher
(:Slot {
    slotId: string,            // "{storageId}_Slot_{index}"
    index: int,
    carrierId: string?,
    carrierType: string?,
    productType: string?,
    productId: string?,
    isEmpty: boolean,
    lastUpdated: datetime
})
```

#### Asset (bestehendes Label aus AAS-Daten)
```cypher
(:Asset {
    shell_id: string,          // z.B. "P102"
    // weitere AAS-Properties...
})
```

### 2.2 Relationships

#### IS_SUBAGENT_OF
**Hierarchische Agentenstruktur**
```cypher
(:Agent:PlanningHolon)-[:IS_SUBAGENT_OF]->(:Agent:ModuleHolon)
(:Agent:ExecutionHolon)-[:IS_SUBAGENT_OF]->(:Agent:ModuleHolon)
(:Agent:ModuleHolon)-[:IS_SUBAGENT_OF]->(:Agent:DispatchingAgent)
```

**Properties:**
- `registeredAt: datetime` - Wann wurde die Beziehung hergestellt
- `role: string` - "planning", "execution", etc.

#### MANAGES_ASSET
**ModuleHolon verwaltet physisches Asset**
```cypher
(:Agent:ModuleHolon)-[:MANAGES_ASSET]->(:Asset)
```

**Properties:**
- `since: datetime` - Seit wann wird verwaltet

#### MANAGES_NAMESPACE
**DispatchingAgent verwaltet Namespace**
```cypher
(:Agent:DispatchingAgent)-[:MANAGES_NAMESPACE {name: "phuket"}]->(:Namespace)
```

Alternative (wenn Namespace kein Node):
```cypher
(:Agent:DispatchingAgent {namespace: "phuket", managesNamespace: true})
```

#### MANAGES_PRODUCT
**ProductAgent verwaltet Produkt**
```cypher
(:Agent:ProductAgent)-[:MANAGES_PRODUCT {productId: "12345"}]->(:Product)
```

#### HAS_STORAGE
**ModuleHolon besitzt Storage-Einheiten**
```cypher
(:Agent:ModuleHolon)-[:HAS_STORAGE]->(:Storage)
```

#### HAS_SLOT
**Storage enthält Slots**
```cypher
(:Storage)-[:HAS_SLOT {index: 0}]->(:Slot)
```

### 2.3 Beispiel-Graph (P102 mit Sub-Holons)

```
(DispatchingAgent_phuket:Agent:DispatchingAgent {namespace: "phuket"})
    ^
    | [:IS_SUBAGENT_OF]
    |
(P102:Agent:ModuleHolon {agentId: "P102", moduleName: "AssemblyStation"})
    |--[:MANAGES_ASSET]-->(P102_Asset:Asset {shell_id: "P102"})
    |                          |--[:HAS_POSITION]-->(Position {X: 40, Y: 20})
    |
    |--[:HAS_STORAGE]-->(InputConveyor:Storage {name: "InputConveyor", freeSlots: 3})
    |                       |--[:HAS_SLOT]-->(Slot_0:Slot {isEmpty: true})
    |                       |--[:HAS_SLOT]-->(Slot_1:Slot {isEmpty: false, productId: "WP_001"})
    |
    |--[:HAS_STORAGE]-->(OutputConveyor:Storage {name: "OutputConveyor", freeSlots: 2})
    |
    ^--[:IS_SUBAGENT_OF]--(P102_Planning:Agent:PlanningHolon {moduleId: "P102"})
    ^--[:IS_SUBAGENT_OF]--(P102_Execution:Agent:ExecutionHolon {moduleId: "P102"})
```

---

## 3. Synchronisations-Strategie

### 3.1 Lifecycle Events

#### Agent Registration → CREATE/UPDATE Agent Node
**Trigger:** RegisterMessage empfangen  
**Aktionen:**
1. `MERGE` Agent-Node mit agentId
2. Update Properties (lastRegistration, lastSeen, isActive=true)
3. `MERGE` IS_SUBAGENT_OF Relationship zum Parent
4. Wenn ModuleHolon: `MERGE` MANAGES_ASSET Relationship zu existierendem Asset

#### Agent Deregistration/Stale → SET isActive=false
**Trigger:**
- Explizite Deregister-Nachricht (falls implementiert)
- Timeout (DispatchingAgent.PruneStaleModules)
- Agent-Shutdown

**Aktionen:**
1. `MATCH` Agent by agentId
2. `SET isActive = false`
3. Optional: `DELETE` Node nach Grace-Period (z.B. 24h)

#### Inventory Update → CREATE/UPDATE Storage & Slots
**Trigger:** InventoryMessage empfangen  
**Aktionen:**
1. `MATCH` Agent by moduleId
2. Für jede StorageUnit:
   - `MERGE` Storage-Node
   - Update freeSlots/occupiedSlots
   - `MERGE` HAS_STORAGE Relationship
3. Optional: Slot-Details syncen

### 3.2 Query-Operationen (für BT-Nodes)

#### SyncAgentToNeo4j
```cypher
MERGE (a:Agent {agentId: $agentId})
SET 
  a.agentType = $agentType,
  a.namespace = $namespace,
  a.lastRegistration = datetime($timestamp),
  a.lastSeen = datetime($timestamp),
  a.isActive = true

WITH a
OPTIONAL MATCH (parent:Agent {agentId: $parentAgentId})
WHERE $parentAgentId IS NOT NULL
MERGE (a)-[r:IS_SUBAGENT_OF]->(parent)
SET r.registeredAt = datetime($timestamp)

WITH a
OPTIONAL MATCH (asset:Asset {shell_id: $agentId})
WHERE $agentType = 'ModuleHolon'
MERGE (a)-[m:MANAGES_ASSET]->(asset)
SET m.since = datetime($timestamp)
```

#### SyncInventoryToNeo4j
```cypher
MATCH (agent:Agent {agentId: $moduleId})
MERGE (s:Storage {storageId: $storageId})
SET 
  s.name = $storageName,
  s.moduleId = $moduleId,
  s.totalSlots = $totalSlots,
  s.freeSlots = $freeSlots,
  s.occupiedSlots = $occupiedSlots,
  s.lastUpdated = datetime($timestamp)

MERGE (agent)-[:HAS_STORAGE]->(s)
```

#### DeregisterAgentFromNeo4j
```cypher
MATCH (a:Agent {agentId: $agentId})
SET a.isActive = false, a.lastSeen = datetime($timestamp)

// Optional: nach Grace-Period löschen
WITH a
WHERE datetime($timestamp) - a.lastSeen > duration({hours: 24})
DETACH DELETE a
```

---

## 4. Implementierungs-Plan (BT-Nodes)

### 4.1 SyncAgentToNeo4jNode
**Input (Blackboard):**
- `MessagingClient` (für empfangene RegisterMessage)
- `LastReceivedMessage` (I40Message mit RegisterMessage)
- `Neo4jDriver` (von InitNeo4jNode)

**Logik:**
1. Parse RegisterMessage
2. Determine AgentType from AgentRole
3. Resolve ParentAgentId
4. Execute MERGE Query
5. Log Success/Failure

**XML-Verwendung:**
```xml
<SyncAgentToNeo4j name="SyncAgentToGraph" />
```

### 4.2 SyncInventoryToNeo4jNode
**Input (Blackboard):**
- `LastReceivedMessage` (mit InventoryMessage)
- `Neo4jDriver`

**Logik:**
1. Parse InventoryMessage → List<StorageUnit>
2. Für jede StorageUnit: MERGE Storage-Node
3. Optional: Sync Slot-Details

**XML-Verwendung:**
```xml
<SyncInventoryToNeo4j name="SyncInventoryToGraph" />
```

### 4.3 DeregisterAgentFromNeo4jNode
**Input (Blackboard):**
- `AgentId` (zu deregistrierender Agent)
- `Neo4jDriver`

**Logik:**
1. SET isActive = false
2. Optional: Schedule Delete nach Grace-Period

**Verwendung:**
- In Shutdown-Tree
- In DispatchingAgent nach PruneStaleModules

### 4.4 Integration in bestehende Trees

#### ModuleHolon Tree (P102)
```xml
<Sequence name="RegisterAndSync">
  <RegisterAgent />
  <SyncAgentToNeo4j />
</Sequence>

<Sequence name="InventoryUpdate">
  <UpdateInventoryFromAction />
  <SyncInventoryToNeo4j />
</Sequence>
```

#### DispatchingAgent Tree
```xml
<Sequence name="HandleRegistration">
  <HandleRegistrationNode />
  <SyncAgentToNeo4j />  <!-- Sync des eingehenden Moduls -->
</Sequence>

<Sequence name="SelfRegister">
  <RegisterAgent />
  <SyncAgentToNeo4j />  <!-- Sync des DispatchingAgent selbst -->
</Sequence>
```

---

## 5. Test-Strategie: P102 Synchronisation

### 5.1 Test-Szenario
**Ziel:** P102 ModuleHolon + Sub-Holons in Neo4j syncen und verifizieren

**Schritte:**
1. Setup: Neo4j-Verbindung, MQTT-Client, Test-Context
2. Simuliere RegisterMessages:
   - P102_Planning → P102 (Topic: `phuket/P102/Register`)
   - P102_Execution → P102 (Topic: `phuket/P102/Register`)
   - P102 → DispatchingAgent (Topic: `phuket/DispatchingAgent/Register`)
3. Simuliere InventoryMessage von P102
4. Execute SyncAgentToNeo4jNode für jede Nachricht
5. Execute SyncInventoryToNeo4jNode
6. **Verify Graph:**
   - `MATCH (p:Agent:ModuleHolon {agentId: "P102"}) RETURN p`
   - `MATCH (p)-[:IS_SUBAGENT_OF]->(parent) RETURN parent.agentId`
   - `MATCH (p)-[:MANAGES_ASSET]->(asset:Asset) RETURN asset.shell_id`
   - `MATCH (p)-[:HAS_STORAGE]->(s:Storage) RETURN s.name, s.freeSlots`
7. Cleanup: Löschen der Test-Nodes

### 5.2 Test-Implementierung (Pseudo-Code)
```csharp
[Fact]
public async Task P102_Agent_Synchronization_CreatesGraphStructure()
{
    // Arrange
    var driver = await CreateNeo4jDriver();
    var context = CreateTestContext("P102", "ModuleHolon");
    context.Set("Neo4jDriver", driver);
    
    // 1. Register Sub-Holons
    var planningRegMsg = CreateRegisterMessage("P102_Planning", subagents: [], capabilities: []);
    context.Set("LastReceivedMessage", planningRegMsg);
    context.Set("config.Agent.ParentAgent", "P102");
    
    var syncNode = new SyncAgentToNeo4jNode { Context = context };
    Assert.Equal(NodeStatus.Success, await syncNode.Execute());
    
    // 2. Register P102 itself
    var p102RegMsg = CreateRegisterMessage("P102", subagents: ["P102_Planning", "P102_Execution"], capabilities: ["Assemble"]);
    context.Set("LastReceivedMessage", p102RegMsg);
    context.Set("config.Agent.ParentAgent", "DispatchingAgent");
    
    Assert.Equal(NodeStatus.Success, await syncNode.Execute());
    
    // 3. Sync Inventory
    var invMsg = CreateInventoryMessage("P102", storages: [
        new StorageUnit { Name = "InputConveyor", Slots = [...] }
    ]);
    context.Set("LastReceivedMessage", invMsg);
    
    var invSyncNode = new SyncInventoryToNeo4jNode { Context = context };
    Assert.Equal(NodeStatus.Success, await invSyncNode.Execute());
    
    // Verify Graph
    var session = driver.AsyncSession();
    var result = await session.RunAsync(
        "MATCH (p:Agent:ModuleHolon {agentId: $agentId})-[:MANAGES_ASSET]->(asset:Asset) RETURN asset.shell_id, p.agentId",
        new { agentId = "P102" });
    
    var record = await result.SingleAsync();
    Assert.Equal("P102", record["p.agentId"].As<string>());
    Assert.Equal("P102", record["asset.shell_id"].As<string>());
    
    // Cleanup
    await session.RunAsync("MATCH (a:Agent {agentId: $id}) DETACH DELETE a", new { id = "P102" });
}
```

---

## 6. Offene Punkte & Entscheidungen

### 6.1 Capabilities in Graph?
**Anforderung:** "Capabilities müssen nicht in den Graphen übertragen werden."

**Lösung:** Capabilities NUR im Agent-Node als String-Array speichern:
```cypher
(:Agent {capabilities: ["Assemble", "Drill"]})
```
Kein separater Capability-Node, keine PROVIDES_CAPABILITY Relationship zu Asset.

### 6.2 Namespace als Node oder Property?
**Option A:** Namespace-Node
```cypher
(ns:Namespace {name: "phuket"})<-[:MANAGES_NAMESPACE]-(disp:DispatchingAgent)
```

**Option B:** Property am DispatchingAgent
```cypher
(disp:DispatchingAgent {namespace: "phuket", managesNamespace: true})
```

**Empfehlung:** Option B (einfacher, weniger Nodes)

### 6.3 Slot-Details in Graph?
**Entscheidung:** Optional, nur wenn explizit benötigt  
- Storage-Aggregate (freeSlots/occupiedSlots) immer syncen
- Einzelne Slots nur bei Bedarf (z.B. für Routing-Queries)

### 6.4 Grace-Period für Deregistration?
**Empfehlung:** 
- Sofort `SET isActive = false`
- `DETACH DELETE` nach 24h (optional)
- Ermöglicht Historie-Queries

---

## 7. Migration & Rollout

1. **Phase 1:** Implementierung der Sync-Nodes (ohne Änderung bestehender Trees)
2. **Phase 2:** Integration-Tests (dieser Test-Plan)
3. **Phase 3:** Hinzufügen in ModuleHolon-Trees (optional, manuell aktivierbar)
4. **Phase 4:** Aktivierung in Produktions-Configs

**Backward-Kompatibilität:** 
- Bestehende Trees funktionieren weiter ohne Neo4j-Sync
- Sync-Nodes sind optional (fail-safe bei fehlendem Driver)

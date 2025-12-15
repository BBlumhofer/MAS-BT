# MAS-BT - Multi-Agent System mit Behavior Trees

## Inhaltsverzeichnis

1. [Überblick](#überblick)
2. [Architektur](#architektur)
3. [Agenten-Typen](#agenten-typen)
4. [Installation und Ausführung](#installation-und-ausführung)
5. [ProcessChain-Workflow](#processchain-workflow)
6. [Planning und Execution](#planning-und-execution)
7. [Konfiguration](#konfiguration)
8. [Entwicklung](#entwicklung)
9. [Troubleshooting](#troubleshooting)

---

## Überblick

**MAS-BT** ist ein holonisches Multi-Agenten-System für flexible Produktionssysteme, das auf **Behavior Trees** (BT) basiert. Jeder Agent (Resource, Transport, Module, Product, Dispatching) wird durch einen Behavior Tree gesteuert, der seine Entscheidungslogik, Kommunikation und Ausführung orchestriert.

### Kernmerkmale

- **Behavior Tree-gesteuerte Agenten**: Alle Entscheidungen und Aktionen werden über XML-definierte BTs modelliert
- **Holonische Architektur**: Hierarchische Agenten mit Planning und Execution Sub-Holons
- **OPC UA Integration**: Echtzeit-Kommunikation mit Maschinen (Skills, Zustandsüberwachung, Inventar)
- **AAS-basierte Semantik**: Asset Administration Shell für Prozessdefinitionen, Capability Descriptions, Produktionspläne
- **MQTT Messaging**: Asynchrone Kommunikation zwischen Agenten (I4.0 Sharp Messaging)
- **ProcessChain-Generierung**: Automatische Erstellung von Prozessketten aus Required Capabilities
- **Precondition-basierte Ausführung**: Skill-Requests werden erst ausgeführt, wenn alle Vorbedingungen erfüllt sind

### Technologie-Stack

- **.NET 10.0**: Laufzeitumgebung
- **BehaviorTree.CPP** (Format 4): XML-basierte Behavior Tree Definition
- **OPC UA**: Maschinen-Schnittstelle (via Skill-Sharp-Client)
- **AAS (Asset Administration Shell)**: Semantische Datenmodelle (via AAS-Sharp-Client)
- **MQTT**: Inter-Agent Messaging (via I4.0-Sharp-Messaging)
- **Neo4j**: Capability-Matching und Ähnlichkeitsanalyse

---

## Architektur

### Systemübersicht

```
┌─────────────────────────────────────────────────────────────────┐
│                        Product Agent                             │
│  (ProductionPlan, RequiredCapabilities, Scheduling)             │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Dispatching Agent                            │
│  (ProcessChain Generation, Capability Matching, Routing)        │
└────────────────────────────┬────────────────────────────────────┘
                             │
         ┌───────────────────┴────────────────────┐
         ▼                                        ▼
┌──────────────────────┐               ┌──────────────────────┐
│   Module Holon P102  │               │   Module Holon P103  │
│  ┌────────────────┐  │               │  ┌────────────────┐  │
│  │ Planning Agent │  │               │  │ Planning Agent │  │
│  │ (Offers,       │  │               │  │ (Scheduling,   │  │
│  │  Scheduling)   │  │               │  │  Constraints)  │  │
│  └───────┬────────┘  │               │  └───────┬────────┘  │
│          │           │               │          │           │
│          ▼           │               │          ▼           │
│  ┌────────────────┐  │               │  ┌────────────────┐  │
│  │Execution Agent │  │               │  │Execution Agent │  │
│  │ (SkillRequest, │  │               │  │ (Preconditions,│  │
│  │  Monitoring)   │  │               │  │  Queue)        │  │
│  └───────┬────────┘  │               │  └───────┬────────┘  │
└──────────┼───────────┘               └──────────┼───────────┘
           │                                      │
           ▼                                      ▼
    ┌────────────┐                        ┌────────────┐
    │  OPC UA    │                        │  OPC UA    │
    │  Server    │                        │  Server    │
    │  (P102)    │                        │  (P103)    │
    └────────────┘                        └────────────┘
```

### Integration-Layer

| Layer | Technologie | Zweck |
|-------|-------------|-------|
| **Execution Layer** | OPC UA | Echtzeit-Maschinen-Kommunikation, Skill-Ausführung |
| **Semantic Layer** | AAS (Asset Administration Shell) | Prozesswissen, Capability Descriptions, Pläne |
| **Control Layer** | Behavior Trees | Agenten-Entscheidungslogik, Orchestrierung |
| **Coordination Layer** | MQTT (I4.0 Messaging) | Inter-Agent Kommunikation, Events |

---

## Agenten-Typen

### 1. Dispatching Agent

**Zweck**: Koordiniert mehrere Module unter einem Namespace, erstellt ProcessChains und vermittelt zwischen Product Agents und Modulen.

**Hauptfunktionen**:
- **ProcessChain-Generierung**: Findet Modul-Kandidaten für Required Capabilities
- **Capability Matching**: Nutzt Neo4j-basierte Ähnlichkeitsanalyse
- **Offer-Verhandlung**: Sammelt Angebote von Planning Agents
- **Modul-Registrierung**: Verwaltet Registry von verfügbaren Modulen und deren Capabilities

**Behavior Tree**: `Trees/DispatchingAgent.bt.xml`

**Konfiguration**: `configs/dispatching_agent.json`

**Topics**:
- `/{Namespace}/ProcessChain` (Request/Response für ProcessChain)
- `/{Namespace}/DispatchingAgent/Offer` (CfP und Proposals)
- `/{Namespace}/DispatchingAgent/register` (Modul-Registrierung)

**Siehe**: `docs/predefined_agents/dispatching_agent/DispatchingAgent.md`

---

### 2. Planning Agent

**Zweck**: Planeines einzelnen Moduls. Erstellt Angebote (Offers) für Capabilities, verwaltet MachineSchedule, dispatcht SkillRequests an Execution Agent.

**Hauptfunktionen**:
- **Capability Matching**: Prüft ob angefragte Capability unterstützt wird
- **Feasibility Check**: Prüft Constraints (Material, Tools, Storage)
- **Offer-Erstellung**: Berechnet EarliestSchedulingInformation, Cost, Actions
- **Scheduling**: Verwaltet ProductionPlan und dispatcht Actions zur Execution
- **Transport-Anfragen**: Koordiniert mit Dispatching Agent für Transporte

**Behavior Tree**: `Trees/PlanningAgent.bt.xml`

**Konfiguration**: `configs/planning_agent.json` (oder Module-spezifisch unter `configs/specific_configs/Module_configs/<ModuleId>/`)

**Topics**:
- `/{Namespace}/{ModuleId}/PlanningAgent/OfferRequest` (empfängt CfPs)
- `/{Namespace}/DispatchingAgent/Offer` (sendet Proposals)
- `/{Namespace}/{ModuleId}/SkillRequest` (sendet an Execution)
- `/{Namespace}/{ModuleId}/SkillResponse` (empfängt von Execution)

**Wichtige Nodes**:
- `ParseCapabilityRequest`: Parst CfP
- `CapabilityMatchmaking`: Neo4j-basiertes Matching
- `FeasibilityCheck`: Constraint-Prüfung
- `PlanCapabilityOffer`: Erstellt Offer mit Action und Scheduling
- `SendCapabilityOffer`: Publiziert Offer
- `SelectSchedulableAction`: Wählt nächste Action aus ProductionPlan
- `SendSkillRequest`: Sendet SkillRequest an Execution
- `AwaitSkillResponse` / `ApplySkillResponse`: Verarbeitet Response

---

### 3. Execution Agent

**Zweck**: Führt Skills auf der Maschine aus. Empfängt SkillRequests vom Planning Agent, prüft Preconditions, führt Skills via OPC UA aus, publiziert ActionState-Updates.

**Hauptfunktionen**:
- **Precondition-Prüfung**: Coupled, Locked, StartupSkill running, InStorage
- **Skill-Ausführung**: Via OPC UA (ExecuteSkill, WaitForCompletion, ResetSkill)
- **Queue-Verwaltung**: SkillRequestQueue mit Backoff bei nicht erfüllten Preconditions
- **Monitoring**: CheckReadyState, Inventory, Neighbors, Lock-Status
- **ActionState Publishing**: consent, inform (EXECUTING), inform (DONE), failure

**Behavior Tree**: `Trees/ExecutionAgent.bt.xml`

**Konfiguration**: `configs/Execution_agent.json` (oder Module-spezifisch)

**Topics**:
- `/{Namespace}/{ModuleId}/SkillRequest` (empfängt von Planning)
- `/{Namespace}/{ModuleId}/SkillResponse` (sendet an Planning)
- `/{Namespace}/{ModuleId}/Inventory` (publiziert Storage-Änderungen)
- `/{Namespace}/{ModuleId}/ActionQueue` (publiziert Queue-Snapshots)

**Wichtige Nodes**:
- `ReadMqttSkillRequest`: Dequeue aus SkillRequestQueue
- `CheckSkillPreconditions`: Evaluiert Preconditions
- `SendSkillResponse`: Publiziert ActionState (consent/refusal/inform/failure)
- `ExecuteSkill`: Führt Skill via OPC UA aus
- `UpdateInventory`: Liest Storage und publiziert via MQTT
- `ReadNeighborsFromRemote` / `PublishNeighbors`: Neighbor-Informationen

**Precondition-Verhalten**:
- Wenn Preconditions nicht erfüllt → Request wird requeued (Backoff gesetzt)
- `MaxPreconditionRetries` (default 10) begrenzt Requeue-Versuche
- `PreconditionBackoffStartMs` (default 5000ms) mit exponentiellem Backoff

**Siehe**: `docs/predefined_agents/execution_agent/MONITORING_AND_SKILL_NODES.md`, `docs/predefined_agents/execution_agent/EXECUTION_AGENT_TODO.md`

---

### 4. Product Agent

**Zweck**: Steuert die Fertigung eines Produkts. Fragt ProcessChain beim Dispatching Agent an, verwaltet ProductionPlan, überwacht Ausführung.

**Hauptfunktionen**:
- **ProcessChain-Anfrage**: Sendet RequiredCapabilities an Dispatching Agent
- **ProductionPlan-Verwaltung**: Speichert Steps, Actions, Status
- **Scheduling-Überwachung**: Überwacht ActionState-Updates

**Behavior Tree**: `Trees/ProductAgent.bt.xml`

**Konfiguration**: `configs/product_agent.json`

**Topics**:
- `/{Namespace}/ProcessChain` (Request für ProcessChain)

---

### 5. Module Holon (Router)

**Zweck**: Wrapper-Agent, der Planning und Execution eines Moduls kombiniert und beim Dispatching Agent registriert.

**Hauptfunktionen**:
- **Registrierung**: Publiziert Modul-Info (AgentId, Subagents, Capabilities) an Dispatching Agent
- **Routing**: Leitet Offers/Scheduling/Booking zwischen Dispatcher und Planning/Execution weiter

**Behavior Tree**: `Trees/ModuleHolon.bt.xml`

**Siehe**: `docs/predefined_agents/module_agent/ModuleHolon.md`

---

## Installation und Ausführung

### Voraussetzungen

- .NET 10.0 SDK
- MQTT Broker (z.B. Mosquitto)
- OPC UA Server (für Module)
- AAS Repository (Shell & Submodel Repositories)
- Neo4j (optional, für Capability Matching)

### Build

```bash
cd /path/to/MAS-BT
dotnet build MAS-BT.csproj
```

### Tests ausführen

```bash
dotnet test MAS-BT.csproj
```

### Agenten starten

Der Standard-Runner ist `Examples/ModuleInitializationTestRunner`, der von `Program.cs` gestartet wird.

#### Dispatching Agent

```bash
dotnet run -- configs/dispatching_agent.json
```

Oder mit Behavior Tree direkt:

```bash
dotnet run -- Trees/DispatchingAgent.bt.xml
```

(Hinweis: Der Runner lädt standardmäßig `config.json` aus der Repository-Root. Wenn du eine spezifische Config verwenden willst, kopiere/linke sie nach `config.json` oder übergebe sie als Argument.)

#### Planning Agent

```bash
# Kopiere Config nach config.json
cp configs/planning_agent.json config.json
dotnet run -- Trees/PlanningAgent.bt.xml
```

Oder:

```bash
dotnet run -- configs/planning_agent.json
```

Oder mit Symlink:

```bash
ln -s configs/planning_agent.json config.json
dotnet run -- Trees/PlanningAgent.bt.xml
```

Modul-spezifische Configs liegen unter `configs/specific_configs/Module_configs/<ModuleId>/`, z.B.:

```bash
dotnet run -- configs/specific_configs/Module_configs/P103/P103_Planning_agent.json
```

#### Execution Agent

```bash
cp configs/Execution_agent.json config.json
dotnet run -- Trees/ExecutionAgent.bt.xml
```

Oder:

```bash
dotnet run -- configs/Execution_agent.json
```

Modul-spezifische Configs:

```bash
dotnet run -- configs/specific_configs/Module_configs/P102/P102_Execution_agent.json
```

#### Product Agent

```bash
dotnet run -- Trees/ProductAgent.bt.xml
```

(Hinweis: `ProductAgent.bt.xml` lädt intern `configs/product_agent.json` via ReadConfig-Node.)

#### Beispiel: Mehrere Agenten gleichzeitig starten

Terminal 1 (Dispatching Agent):
```bash
dotnet run -- --spawn-terminal configs/dispatching_agent.json
```

Terminal 2 (Planning Agent P102):
```bash
dotnet run -- --spawn-terminal configs/specific_configs/Module_configs/P102/P102_Planning_agent.json
```

Terminal 3 (Execution Agent P102):
```bash
dotnet run -- --spawn-terminal configs/specific_configs/Module_configs/P102/P102_Execution_agent.json
```

(Hinweis: `--spawn-terminal` ist optional und startet den Agent in einem neuen Terminal-Fenster, sofern unterstützt.)

### Sandbox-Umgebung

Für manuelle Tests benötigst du die Sandbox-Umgebung (OPC UA Server + Mosquitto). Diese liegt typischerweise unter `environment/playground-v3`:

```bash
cd environment/playground-v3
docker compose up -d
```

---

## ProcessChain-Workflow

Der vollständige ProcessChain-Workflow ist nun implementiert und getestet.

### 1. Product Agent fragt ProcessChain an

Der Product Agent sendet RequiredCapabilities an den Dispatching Agent:

**Topic**: `/{Namespace}/ProcessChain`

**Message Type**: `callForProposal`

**InteractionElements**:
- `ProcessChainRequest` (SubmodelElementCollection)
  - `ProductId` (Property, xs:string)
  - `RequiredCapabilities` (SubmodelElementList von RequiredCapability)

### 2. Dispatching Agent verarbeitet Request

**Nodes** (siehe `Trees/DispatchingAgent.bt.xml`):
1. `ParseProcessChainRequest`: Parst Request, extrahiert RequiredCapabilities
2. `CheckForCapabilitiesInNamespace`: Prüft ob lokale Module die Capabilities haben
3. `DispatchCapabilityRequests`: Sendet CfPs an Planning Agents der Kandidaten-Module
4. `CollectCapabilityOffer`: Sammelt Offers (mit Timeout, Inbox-Drain, CfP-Reissue)
5. `BuildProcessChainResponse`: Baut ProcessChain aus Offers
6. `SendProcessChainResponse`: Publiziert consent oder refusal

**CfP-Topic**: `/{Namespace}/{ModuleId}/PlanningAgent/OfferRequest`

**Offer-Topic**: `/{Namespace}/DispatchingAgent/Offer`

### 3. Planning Agent erstellt Offer

**Nodes** (siehe `Trees/PlanningAgent.bt.xml`):
1. `WaitForMessage`: Wartet auf CfP auf `/{Namespace}/{ModuleId}/PlanningAgent/OfferRequest`
2. `ParseCapabilityRequest`: Parst CfP (RequiredCapability, RequirementId, ConversationId)
3. `CapabilityMatchmaking`: Neo4j-basiertes Matching (Similarity-Score)
4. `CheckConstraints`: Prüft Material, Tools, Storage
5. `RequestTransport` / `EvaluateRequestTransportResponse`: Fragt Transport beim Dispatching Agent an (optional)
6. `FeasibilityCheck`: Prüft ob Ausführung möglich (Storage, Lock, Coupled)
7. `PlanCapabilityOffer`: Erstellt CapabilityOffer mit:
   - `OfferedCapabilityReference` (ReferenceElement)
   - `InstanceIdentifier` (Property, UUID)
   - `EarliestSchedulingInformation` (SchedulingContainer: PlannedStart, SetupTime, CycleTime)
   - `Station` (Property, AgentId)
   - `Cost` (Property, double)
   - `Actions` (Action-Objekt mit InputParameters, Skill, Preconditions)
8. `SendCapabilityOffer`: Publiziert auf `/{Namespace}/DispatchingAgent/Offer`

**Oder**: `SendPlanningRefusal` bei Nicht-Machbarkeit

### 4. Dispatching Agent baut ProcessChain

**BuildProcessChainResponse**:
- Für jede RequiredCapability wird ein `RequiredCapElement` erstellt
- Jedes Element enthält:
  - `RequiredCapabilityReference` (Referenz auf ursprüngliche Capability)
  - `InstanceIdentifier` (RequirementId)
  - `OfferedCapabilities` (SubmodelElementList der gesammelten Offers)

**Ergebnis**: ProcessChain mit allen verfügbaren Offers pro Requirement

### 5. Product Agent empfängt ProcessChain

Der Product Agent erhält die ProcessChain als `consent` oder `refusal`.

**Nächste Schritte** (noch nicht vollständig implementiert):
- Product Agent wählt beste Offers aus (z.B. nach Cost, EarliestStart)
- Product Agent erstellt ProductionPlan mit ausgewählten Actions
- Product Agent dispatcht Actions an Planning Agents

---

## Planning und Execution

### Planning Agent Workflow

1. **Initialization**:
   - Lädt CapabilityDescription vom AAS
   - Extrahiert Capabilities
   - Publiziert Capabilities
   - Registriert sich beim Dispatching Agent (via ModuleHolon)

2. **Offer-Loop** (Parallel):
   - Wartet auf CfPs
   - Erstellt und sendet Offers

3. **Dispatch-Loop** (Parallel):
   - `SelectSchedulableAction`: Wählt nächste ausführbare Action aus ProductionPlan
   - `SendSkillRequest`: Sendet SkillRequest an Execution Agent
   - `AwaitSkillResponse`: Wartet auf SkillResponse
   - `ApplySkillResponse`: Aktualisiert ActionState im ProductionPlan

### Execution Agent Workflow

1. **Initialization**:
   - Verbindet zu MQTT Broker
   - Verbindet zu OPC UA Server
   - Sperrt Modul (LockResource)
   - Stellt Coupling sicher (EnsurePortsCoupled)
   - Startet StartupSkill
   - Publiziert Inventory

2. **SkillRequest-Loop** (Parallel):
   - `ReadMqttSkillRequest`: Dequeue aus SkillRequestQueue
   - `CheckModuleState`: Prüft ob Modul frei (nicht EXECUTING)
   - `CheckSkillPreconditions`: Prüft Preconditions
     - Standard: Coupled, Locked, StartupSkill running
     - Action-spezifisch: InStorage (SlotContentType, SlotValue)
   - `SendSkillResponse` (consent): Bestätigt Ausführung
   - `SetModuleState` (EXECUTING)
   - `SendSkillResponse` (inform, EXECUTING)
   - `ExecuteSkill`: Führt Skill via OPC UA aus
   - `MonitoringSkill`: Überwacht Skill-Status
   - `ResetSkill`: Resetet Skill nach Completion
   - `UpdateInventory`: Aktualisiert und publiziert Storage
   - `SetModuleState` (DONE)
   - `SendSkillResponse` (inform, DONE)

3. **Registration-Heartbeat** (Parallel):
   - Registriert sich periodisch beim Dispatching Agent
   - Publiziert Inventory und Neighbors

### Precondition-Retry

Wenn Preconditions nicht erfüllt sind:
1. Request wird an Ende der Queue verschoben
2. `NextRetryUtc` wird gesetzt (Backoff: `PreconditionBackoffStartMs` * 2^attempts)
3. Keine ERROR-Response wird gesendet (Request bleibt in Queue)
4. Nach `MaxPreconditionRetries` Versuchen → ERROR und Dequeue

**Queue-Snapshots** werden auf `/{Namespace}/{ModuleId}/ActionQueue` publiziert.

---

## Konfiguration

### Config-Struktur

Alle Configs folgen diesem Schema:

```json
{
  "Agent": {
    "AgentId": "P102_Planning",
    "Role": "PlanningAgent",
    "ModuleId": "P102",
    "ModuleName": "P102",
    "RegistrationIntervalMs": 5000
  },
  "Namespace": "phuket",
  "MQTT": {
    "Broker": "localhost",
    "Port": 1883,
    "ClientId": "{AgentId}_{Role}",
    "TopicPrefix": "/phuket"
  },
  "OPCUA": {
    "Endpoint": "opc.tcp://localhost:4840"
  },
  "AAS": {
    "ShellRepositoryEndpoint": "http://localhost:8081/shells",
    "SubmodelRepositoryEndpoint": "http://localhost:8081/submodels"
  },
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "User": "neo4j",
    "Password": "password"
  },
  "Execution": {
    "PreconditionBackoffStartMs": 5000,
    "MaxPreconditionRetries": 10
  }
}
```

### Wichtige Config-Keys

- **`AgentId`**: Eindeutige ID des Agenten
- **`Role`**: `DispatchingAgent`, `PlanningAgent`, `ExecutionAgent`, `ProductAgent`, `ModuleHolon`
- **`ModuleId`** / **`ModuleName`**: OPC UA Modul-Name (für Planning/Execution)
- **`Namespace`**: Hierarchischer Namespace (z.B. `phuket`, `Company/Factory1`)
- **`MQTT.ClientId`**: Wird aus Platzhaltern gebildet (`{AgentId}`, `{Role}`, `{ModuleId}`)
- **`PreconditionBackoffStartMs`**: Initial Backoff bei Precondition-Retry
- **`MaxPreconditionRetries`**: Max Anzahl Requeue-Versuche

### Config-Laden

**Variante 1**: Config als Argument übergeben:
```bash
dotnet run -- configs/my_config.json
```

**Variante 2**: Config nach `config.json` kopieren/linken:
```bash
cp configs/my_config.json config.json
dotnet run -- Trees/MyAgent.bt.xml
```

**Variante 3**: Im BT mit `ReadConfig` laden:
```xml
<ReadConfig name="LoadConfig" configPath="configs/my_config.json" />
```

**Context-Keys nach ReadConfig**:
- `config.Agent.AgentId`, `config.Agent.Role`, `config.Namespace`, ...
- Zugriff im BT: `{config.Agent.AgentId}`

**Siehe**: `docs/STARTUP_AND_MQTT.md` für Details zu ClientId-Bildung und MQTT-Diagnostics.

---

## Entwicklung

### Projekt-Struktur

```
MAS-BT/
├── BehaviorTree/              # BT Core (Engine, Context, Serialization)
├── Nodes/                     # BT Node Implementations
│   ├── Common/                # Shared Utilities
│   ├── Configuration/         # Config, Connect, Init Nodes
│   ├── Constraints/           # Constraint-Check Nodes
│   ├── Dispatching/           # Dispatching Agent Nodes
│   │   └── ProcessChain/      # ProcessChain-spezifische Nodes
│   ├── Locking/               # Lock/Unlock Nodes
│   ├── Messaging/             # Send/Wait Message Nodes
│   ├── ModuleHolon/           # Module Holon Router Nodes
│   ├── Monitoring/            # Monitoring Nodes (Inventory, State, Neighbors)
│   ├── Planning/              # Planning Nodes (Capability, Feasibility, Offer)
│   ├── Recovery/              # Recovery Nodes
│   └── SkillControl/          # ExecuteSkill, WaitForSkillState, ResetSkill
├── Services/                  # Shared Services (AAS Client, MQTT, OPC UA)
├── Tools/                     # External Tools (ProcessChainGenerator)
├── Trees/                     # Behavior Tree XML Definitions
├── Examples/                  # Test Runners
├── configs/                   # Configuration Files
│   ├── generic_configs/       # Template Configs
│   └── specific_configs/      # Module-specific Configs
├── docs/                      # Documentation
└── tests/                     # Unit/Integration Tests
```

### Neue Nodes hinzufügen

1. **Erstelle Node-Klasse** unter `Nodes/<Category>/<YourNode>.cs`:
   ```csharp
   public class YourNode : BTNode
   {
       public override NodeStatus Execute(BTContext context, ILogger logger)
       {
           // Your logic
           return NodeStatus.Success;
       }
   }
   ```

2. **Registriere Node** in `BehaviorTree/Serialization/NodeRegistry.cs`:
   ```csharp
   Register<YourNode>("YourNode");
   ```

3. **Nutze Node im BT**:
   ```xml
   <YourNode name="MyInstance" InputParam="{context.Key}" />
   ```

4. **Dokumentiere Node** in `specs.json` und entsprechender `.md`-Datei.

### BT-Entwicklung

**Best Practices**:
- **Kleine, wiederverwendbare Nodes**: Jeder Node hat eine klare Verantwortlichkeit
- **Context-basierte Kommunikation**: Shared State über `BTContext` (Get/Set)
- **Naming Convention**: `module_{ModuleName}_ready`, `skill_{SkillName}_state`, `CurrentAction`, `CapabilityDescription_{AgentId}`
- **Logging**: Nutze `logger` für Diagnostics
- **Cleanup**: Implementiere `OnAbort`/`OnReset` für Composite/Decorator Nodes

**Debugging**:
- Logs werden auf Console ausgegeben (wenn MQTT nicht erreichbar)
- MQTT-basierte Logs auf `/{Namespace}/{AgentId}/Logs`
- Nutze `Examples/ModuleInitializationTestRunner` für Step-by-Step Debugging

**Siehe**: Repository Custom Instructions in diesem Dokument für detaillierte BT-Patterns.

---

## Troubleshooting

### MQTT Verbindungsprobleme

**Symptom**: `ConnectToMessagingBroker` schlägt fehl

**Lösungen**:
- Prüfe ob MQTT Broker läuft: `mosquitto -v`
- Prüfe Broker-Adresse in Config: `MQTT.Broker`, `MQTT.Port`
- Prüfe Firewall-Regeln
- Teste Verbindung mit `mosquitto_pub`/`mosquitto_sub`

**Siehe**: `docs/STARTUP_AND_MQTT.md`

### OPC UA Verbindungsprobleme

**Symptom**: `ConnectToModule` schlägt fehl

**Lösungen**:
- Prüfe ob OPC UA Server läuft
- Prüfe Endpoint in Config: `OPCUA.Endpoint`
- Prüfe Zertifikate (sofern Security aktiviert)
- OPC UA SDK kann transienten `Bad` Status melden → wird automatisch reconnected

### Preconditions werden nicht erfüllt

**Symptom**: SkillRequest wird immer wieder requeued

**Lösungen**:
- Prüfe Lock-Status: `CheckLockedState`
- Prüfe Coupling: `EnsurePortsCoupled`
- Prüfe StartupSkill: `WaitForSkillState` (Running)
- Prüfe InStorage Precondition: `UpdateInventory` und Logs
- Erhöhe `MaxPreconditionRetries` in Config
- Reduziere `PreconditionBackoffStartMs` für schnelleres Retry

### ProcessChain-Verhandlung scheitert

**Symptom**: Dispatching Agent sendet `refusal`

**Lösungen**:
- Prüfe ob Planning Agents gestartet sind
- Prüfe Module-Registrierung: `/{Namespace}/DispatchingAgent/register`
- Prüfe ob CfPs ankommen: `mosquitto_sub -t "/{Namespace}/{ModuleId}/PlanningAgent/OfferRequest"`
- Prüfe CollectCapabilityOffer Logs (Timeout, Inbox-Drain, Reissue)
- Prüfe Capability-Matching: Neo4j Logs

**Known Issues** (siehe `docs/Progress.md`):
- Proposals können ankommen bevor Callback registriert ist → Inbox-Drain implementiert
- Module registrieren sich nach CfP → CfP Reissue implementiert

### Build Warnings

- **NU1510**: System.Text.Json Pruning Warning → kann ignoriert werden
- **CS0108**: `ProcessParametersValidNode` hides Equals → harmlos

### Tests schlagen fehl

**Symptom**: `dotnet test` schlägt fehl

**Lösungen**:
- Prüfe ob externe Dependencies laufen (MQTT, OPC UA, Neo4j)
- Einige Tests benötigen Sandbox-Umgebung
- Nutze `dotnet build` für Build ohne Tests

---

## Weiterführende Dokumentation

### Agenten

- **Dispatching Agent**: `docs/predefined_agents/dispatching_agent/DispatchingAgent.md`
- **ProcessChain Pattern**: `docs/predefined_agents/dispatching_agent/ProcessChainPattern.md`
- **Module Holon**: `docs/predefined_agents/module_agent/ModuleHolon.md`
- **Execution Agent**: `docs/predefined_agents/execution_agent/MONITORING_AND_SKILL_NODES.md`
- **Execution Agent TODOs**: `docs/predefined_agents/execution_agent/EXECUTION_AGENT_TODO.md`
- **Inventory MQTT**: `docs/predefined_agents/execution_agent/InventoryMQTT.md`

### Konfiguration

- **Configuration Nodes**: `docs/CONFIGURATION_NODES.md`
- **Startup & MQTT**: `docs/STARTUP_AND_MQTT.md`

### Capability Matching

- **Similarity Analysis Agent**: `docs/SimilarityAnalysisAgent.md`
- **Similarity Analysis Message Flow**: `docs/SimilarityAnalysisAgent_MessageFlow.md`
- **Neo4j Capability Matching**: `docs/CapabilityMatching_Neo4j.md`
- **How to See Similarity Results**: `docs/HowToSeeSimilarityResults.md`
- **Similarity Analysis Results**: `docs/SimilarityAnalysisResults.md`

### Entwicklung

- **Node Library Specification**: `specs.json`
- **Progress & Known Issues**: `docs/Progress.md`
- **Konzept**: `docs/Konzept.md`
- **CFP Routing**: `docs/CFP_Routing.md`

---

## Lizenz

(Siehe Repository-Root für Lizenzinformationen)

---

## Kontakt und Support

(Siehe Repository-Root für Kontaktinformationen)

---

**Zuletzt aktualisiert**: 2025-12-15

**Status**: ProcessChain-Workflow vollständig implementiert und getestet. Planning und Execution Agents funktional. Precondition-basierte Queue-Verwaltung implementiert.

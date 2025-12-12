# Dispatching Agent - Konzept und Architektur

## 1. Überblick

Der **Dispatching Agent** ist ein hierarchischer Koordinationsagent im MAS-BT-System, der zwischen Produktagenten und Produktionsmodulen vermittelt. Seine Hauptaufgabe ist die **Grobplanung** und **Routing** von Produktionsaufträgen zu geeigneten Ressourcen.

### 1.1 Position in der Agentenarchitektur

```
Produktagent (Product Holon)
  ↓ (ProcessChain, RequestManufacturingSequence, BookStep)
Dispatching Agent (/Company/Factory/Line/)
    ↓ (RequestOffer, ScheduleAction, BookAction)
PlanningAgent + ExecutionAgent (pro Modul)
    ↓ (SkillRequest/SkillResponse)
OPC UA Skills / Physische Ressource
```

### 1.2 Hierarchische Struktur

Dispatching Agents können **mehrfach geschachtelt** werden, um organisatorische Strukturen abzubilden:

```
DispatchingAgent: /Company/
├── DispatchingAgent: /Company/Factory1/
│   ├── DispatchingAgent: /Company/Factory1/Line1/
│   │   ├── Module: /Company/Factory1/Line1/Module1
│   │   └── Module: /Company/Factory1/Line1/Module2
│   └── DispatchingAgent: /Company/Factory1/Line2/
│       ├── Module: /Company/Factory1/Line2/Module3
│       └── Module: /Company/Factory1/Line2/Module4
└── DispatchingAgent: /Company/Factory2/
    └── ...
```

**Wichtig:** Die **Childs** eines Dispatching Agents können entweder:
- **Module** (Planning + Execution Agent Paare) sein
- **Weitere Dispatching Agents** (bei hierarchischen Namespaces)

---

## 2. Kernfunktionen

### 2.1 Modul-Registrierung

Untergeordnete Module (bzw. deren Planning Agents) registrieren sich beim Dispatching Agent mit folgenden Informationen:

**Registrierungsnachricht (ModuleRegistration):**
```json
{
  "frame": {
    "sender": {
      "identification": { "id": "Module1_Planning_Agent" },
      "role": { "name": "PlanningAgent" }
    },
    "receiver": {
      "identification": { "id": "DispatchingAgent_Line1" },
      "role": { "name": "DispatchingAgent" }
    },
    "type": "inform",
    "conversationId": "registration-Module1-20231210"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "ModuleRegistration",
      "value": [
        {
          "modelType": "Property",
          "idShort": "ModuleId",
          "value": "Module1",
          "valueType": "xs:string"
        },
        {
          "modelType": "Property",
          "idShort": "AasId",
          "value": "https://smartfactory.de/shells/Module1",
          "valueType": "xs:string"
        },
        {
          "modelType": "SubmodelElementList",
          "idShort": "Neighbors",
          "value": [
            { "value": "Module2", "valueType": "xs:string", "modelType": "Property" },
            { "value": "Module3", "valueType": "xs:string", "modelType": "Property" }
          ]
        },
        {
          "modelType": "SubmodelElementCollection",
          "idShort": "CurrentInventory",
          "value": [ /* Storage-Snapshot wie im StateSummary */ ]
        },
        {
          "modelType": "Property",
          "idShort": "State",
          "value": "Active",
          "valueType": "xs:string"
        }
      ]
    }
  ]
}
```

**Aus der Registrierung baut der Dispatching Agent:**
- **Modul-Registry:** Map von ModuleId → ModuleInfo
- **Capability-Index:** Map von CapabilityType → List<ModuleId> (aus AAS CapabilityDescription)
- **Topologie-Graph:** Nachbarschaftsbeziehungen für Transportplanung
- **Inventory-Snapshot:** Aktueller Lagerbestand je Modul

### 2.2 Abruf der AAS-Daten

Der Dispatching Agent lädt für jedes registrierte Modul die **CapabilityDescription** aus dem AAS:

```csharp
// Services/DispatchingAgentService.cs
public async Task<CapabilityDescription> FetchCapabilityDescriptionAsync(string aasId, string moduleId)
{
    var shell = await _aasClient.GetAssetAdministrationShellAsync(aasId);
    var capabilitySM = shell.Submodels.FirstOrDefault(sm => sm.IdShort == "CapabilityDescription");
    // Parse und return
}
```

**CapabilityDescription** enthält:
- Liste der unterstützten Capabilities (z.B. "Drilling", "Screwing", "Painting")
- Constraints pro Capability (Material, Tools, Produkttyp-Einschränkungen)
- Performance-Parameter (Cycle Time, Capacity)

---

## 3. Service-Topics des Dispatching Agents

Der Dispatching Agent bietet **4 zentrale Services** über MQTT Topics an:

### 3.1 ProcessChain

**Topic:** `/{Namespace}/ProcessChain`

**Zweck:** Erstellt eine **Prozesskette** ohne Terminierung – nur mögliche Modul-Kandidaten je Required Capability.

**Request (vom Produktagent):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "ProductHolon_12345" } },
    "receiver": { "identification": { "id": "DispatchingAgent_Line1" } },
    "type": "request",
    "conversationId": "processchain-12345"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "ProcessChainRequest",
      "value": [
        {
          "modelType": "Property",
          "idShort": "ProductId",
          "value": "https://smartfactory.de/shells/Product12345",
          "valueType": "xs:string"
        },
        {
          "modelType": "SubmodelElementList",
          "idShort": "RequiredCapabilities",
          "value": [
            {
              "modelType": "SubmodelElementCollection",
              "idShort": "Capability_0",
              "value": [
                { "idShort": "CapabilityType", "value": "Drilling", "valueType": "xs:string" },
                { "idShort": "QuantityConstraints", "value": "Diameter=5mm", "valueType": "xs:string" }
              ]
            },
            {
              "modelType": "SubmodelElementCollection",
              "idShort": "Capability_1",
              "value": [
                { "idShort": "CapabilityType", "value": "Screwing", "valueType": "xs:string" }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

**Response (vom Dispatching Agent):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "DispatchingAgent_Line1" } },
    "receiver": { "identification": { "id": "ProductHolon_12345" } },
    "type": "consent",  // oder "refuse"
    "conversationId": "processchain-12345"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "ProcessChain",
      "value": [
        {
          "modelType": "SubmodelElementCollection",
          "idShort": "Step_0",
          "value": [
            { "idShort": "RequiredCapability", "value": "Drilling", "valueType": "xs:string" },
            {
              "modelType": "SubmodelElementList",
              "idShort": "CandidateModules",
              "value": [
                { "value": "Module1", "valueType": "xs:string", "modelType": "Property" },
                { "value": "Module3", "valueType": "xs:string", "modelType": "Property" }
              ]
            }
          ]
        },
        {
          "modelType": "SubmodelElementCollection",
          "idShort": "Step_1",
          "value": [
            { "idShort": "RequiredCapability", "value": "Screwing", "valueType": "xs:string" },
            {
              "modelType": "SubmodelElementList",
              "idShort": "CandidateModules",
              "value": [
                { "value": "Module2", "valueType": "xs:string", "modelType": "Property" }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

**Ablauf:**
1. Dispatching Agent prüft **minimale Capability-Matches** im eigenen Capability-Index
2. Falls keine Candidates → `refuse` mit Grund
3. Falls Candidates gefunden → für jeden Step die Module auflisten (noch OHNE Angebote einzuholen)

---

### 3.2 RequestManufacturingSequence

**Topic:** `/DispatchingAgent/{Namespace}/RequestManufacturingSequence/`

**Zweck:** Erstellt eine **terminierte Fertigungssequenz** inkl. Transporte. Bei Annahme werden Schritte als **tentative** im MachineSchedule eingeplant.

**Request (vom Produktagent):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "ProductHolon_12345" } },
    "receiver": { "identification": { "id": "DispatchingAgent_Line1" } },
    "type": "request",
    "conversationId": "mfgseq-12345"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "ManufacturingSequenceRequest",
      "value": [
        {
          "modelType": "Property",
          "idShort": "ProductId",
          "value": "https://smartfactory.de/shells/Product12345",
          "valueType": "xs:string"
        },
        {
          "modelType": "Property",
          "idShort": "Deadline",
          "value": "2025-12-15T14:00:00Z",
          "valueType": "xs:dateTime"
        },
        {
          "modelType": "ReferenceElement",
          "idShort": "ProcessChainReference",
          "value": { /* Verweis auf zuvor generierte ProcessChain */ }
        }
      ]
    }
  ]
}
```

**Response (vom Dispatching Agent):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "DispatchingAgent_Line1" } },
    "receiver": { "identification": { "id": "ProductHolon_12345" } },
    "type": "consent",  // oder "refuse"
    "conversationId": "mfgseq-12345"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "ManufacturingSequence",
      "value": [
        {
          "modelType": "SubmodelElementCollection",
          "idShort": "ScheduledStep_0",
          "value": [
            { "idShort": "StepId", "value": "Step_0", "valueType": "xs:string" },
            { "idShort": "AssignedModule", "value": "Module1", "valueType": "xs:string" },
            { "idShort": "PlannedStart", "value": "2025-12-15T10:00:00Z", "valueType": "xs:dateTime" },
            { "idShort": "PlannedEnd", "value": "2025-12-15T10:15:00Z", "valueType": "xs:dateTime" },
            { "idShort": "BookingStatus", "value": "tentative", "valueType": "xs:string" }
          ]
        },
        {
          "modelType": "SubmodelElementCollection",
          "idShort": "TransportStep_0_1",
          "value": [
            { "idShort": "FromModule", "value": "Module1", "valueType": "xs:string" },
            { "idShort": "ToModule", "value": "Module2", "valueType": "xs:string" },
            { "idShort": "PlannedStart", "value": "2025-12-15T10:15:00Z", "valueType": "xs:dateTime" },
            { "idShort": "PlannedEnd", "value": "2025-12-15T10:20:00Z", "valueType": "xs:dateTime" },
            { "idShort": "BookingStatus", "value": "tentative", "valueType": "xs:string" }
          ]
        },
        {
          "modelType": "SubmodelElementCollection",
          "idShort": "ScheduledStep_1",
          "value": [ /* ... */ ]
        }
      ]
    }
  ]
}
```

**Ablauf:**
1. Dispatching Agent fordert **Angebote** von den Planning Agents der Kandidaten-Module an
2. **Routing-Algorithmus** wählt beste Kombination (z.B. kürzeste Makespan, minimale Transporte)
3. **Transportplanung** fügt Transport-Steps zwischen Modulen ein
4. **Tentative Booking:** Sende `ScheduleAction` (tentative) an Planning Agents der gewählten Module
5. Falls Annahme durch Produkt → Bookings bleiben tentative bis `BookStep`

---

### 3.3 BookStep

**Topic:** `/DispatchingAgent/{Namespace}/BookStep/`

**Zweck:** Konkrete Einplanung eines Schritts im MachineSchedule (von **tentative** → **confirmed**).

**Request (vom Produktagent):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "ProductHolon_12345" } },
    "receiver": { "identification": { "id": "DispatchingAgent_Line1" } },
    "type": "request",
    "conversationId": "bookstep-12345-0"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "BookStepRequest",
      "value": [
        { "idShort": "StepId", "value": "Step_0", "valueType": "xs:string" },
        { "idShort": "ProductId", "value": "https://smartfactory.de/shells/Product12345", "valueType": "xs:string" },
        { "idShort": "ManufacturingSequenceId", "value": "mfgseq-12345", "valueType": "xs:string" }
      ]
    }
  ]
}
```

**Response:**
```json
{
  "frame": {
    "sender": { "identification": { "id": "DispatchingAgent_Line1" } },
    "receiver": { "identification": { "id": "ProductHolon_12345" } },
    "type": "consent",  // oder "refuse"
    "conversationId": "bookstep-12345-0"
  },
  "interactionElements": [
    {
      "modelType": "Property",
      "idShort": "BookingStatus",
      "value": "confirmed",
      "valueType": "xs:string"
    },
    {
      "modelType": "Property",
      "idShort": "ConfirmedStart",
      "value": "2025-12-15T10:00:00Z",
      "valueType": "xs:dateTime"
    }
  ]
}
```

**Ablauf:**
1. Dispatching Agent sendet `BookAction` (confirmed) an den zuständigen Planning Agent
2. Planning Agent bestätigt oder lehnt ab (z.B. wenn Zeitfenster nicht mehr frei)
3. Bei Erfolg: Booking Status → `confirmed`

---

### 3.4 RequestTransportPlan

**Topic:** `/DispatchingAgent/{Namespace}/RequestTransportPlan/`

**Zweck:** Erstellt Transport-Steps zwischen zwei Modulen. Bei Annahme werden Transporte als **tentative** eingeplant.

**Request:**
```json
{
  "frame": {
    "sender": { "identification": { "id": "ProductHolon_12345" } },
    "receiver": { "identification": { "id": "DispatchingAgent_Line1" } },
    "type": "request",
    "conversationId": "transport-12345-0to1"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "TransportRequest",
      "value": [
        { "idShort": "FromModule", "value": "Module1", "valueType": "xs:string" },
        { "idShort": "ToModule", "value": "Module2", "valueType": "xs:string" },
        { "idShort": "CarrierId", "value": "WST_A_13", "valueType": "xs:string" },
        { "idShort": "EarliestStart", "value": "2025-12-15T10:15:00Z", "valueType": "xs:dateTime" }
      ]
    }
  ]
}
```

**Response:**
```json
{
  "frame": {
    "sender": { "identification": { "id": "DispatchingAgent_Line1" } },
    "receiver": { "identification": { "id": "ProductHolon_12345" } },
    "type": "consent",
    "conversationId": "transport-12345-0to1"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "TransportPlan",
      "value": [
        { "idShort": "TransportId", "value": "transport-001", "valueType": "xs:string" },
        { "idShort": "PlannedStart", "value": "2025-12-15T10:15:00Z", "valueType": "xs:dateTime" },
        { "idShort": "PlannedEnd", "value": "2025-12-15T10:20:00Z", "valueType": "xs:dateTime" },
        { "idShort": "Route", "value": "Module1 -> Module2", "valueType": "xs:string" },
        { "idShort": "BookingStatus", "value": "tentative", "valueType": "xs:string" }
      ]
    }
  ]
}
```

**Ablauf:**
1. Dispatching Agent nutzt **Topologie-Graph** (aus Neighbor-Infos) um Route zu finden
2. Falls Transport-Agent existiert → Anfrage an Transport-Agent; sonst direkte Planung
3. Bei Erfolg → Tentative Booking, Produktagent kann akzeptieren/ablehnen

---

## 4. Datenmodelle

### 4.1 ModuleInfo (intern im Dispatching Agent)

```csharp
public class ModuleInfo
{
    public string ModuleId { get; set; }
    public string AasId { get; set; }
    public List<string> Neighbors { get; set; }
    public StorageSnapshot CurrentInventory { get; set; }
    public string State { get; set; } // "Active", "Inactive", ...
    public CapabilityDescription Capabilities { get; set; }
    public DateTime LastRegistration { get; set; }
}
```

### 4.2 ProcessChain

```csharp
public class ProcessChain
{
    public string ProcessChainId { get; set; }
    public string ProductId { get; set; }
    public List<ProcessChainStep> Steps { get; set; }
}

public class ProcessChainStep
{
    public string StepId { get; set; }
    public string RequiredCapability { get; set; }
    public List<string> CandidateModules { get; set; }
    public Dictionary<string, string> CapabilityConstraints { get; set; }
}
```

### 4.3 ManufacturingSequence

```csharp
public class ManufacturingSequence
{
    public string SequenceId { get; set; }
    public string ProductId { get; set; }
    public List<ScheduledStep> Steps { get; set; }
    public DateTime Deadline { get; set; }
}

public class ScheduledStep
{
    public string StepId { get; set; }
    public string AssignedModule { get; set; }
    public DateTime PlannedStart { get; set; }
    public DateTime PlannedEnd { get; set; }
    public string BookingStatus { get; set; } // "tentative" | "confirmed"
    public ScheduledStepType Type { get; set; } // "Production" | "Transport"
    
    // Transport-spezifisch:
    public string FromModule { get; set; }
    public string ToModule { get; set; }
    public string Route { get; set; }
}

public enum ScheduledStepType
{
    Production,
    Transport
}
```

### 4.4 CapabilityIndex (intern)

```csharp
public class CapabilityIndex
{
    private Dictionary<string, List<string>> _capabilityToModules;
    
    public void AddModule(string moduleId, CapabilityDescription capabilities)
    {
        foreach (var cap in capabilities.SupportedCapabilities)
        {
            if (!_capabilityToModules.ContainsKey(cap.Type))
                _capabilityToModules[cap.Type] = new List<string>();
            
            _capabilityToModules[cap.Type].Add(moduleId);
        }
    }
    
    public List<string> FindModulesForCapability(string capabilityType)
    {
        return _capabilityToModules.ContainsKey(capabilityType) 
            ? _capabilityToModules[capabilityType] 
            : new List<string>();
    }
}
```

---

## 5. Kommunikationsprotokolle

### 5.1 Dispatching Agent ↔ Product Agent

**Sequenzdiagramm RequestManufacturingSequence:**

```
ProductAgent                DispatchingAgent           PlanningAgent(Module1)    PlanningAgent(Module2)
    |                               |                           |                        |
    |--RequestManufacturingSeq---->|                           |                        |
    |                               |--RequestOffer------------>|                        |
    |                               |--RequestOffer-------------|----------------------->|
    |                               |                           |                        |
    |                               |<--Offer (Price, Time)-----|                        |
    |                               |<--Offer (Price, Time)-----|------------------------|
    |                               |                           |                        |
    |                               |-- [Routing Algorithm] ----|                        |
    |                               |-- [Select Best Combo] ----|                        |
    |                               |                           |                        |
    |                               |--ScheduleAction(tentative)->|                       |
    |<--ManufacturingSequence-------|                           |                        |
    |                               |                           |                        |
    |--AcceptSequence-------------->|                           |                        |
    |                               |-- [Keep tentative] -------|                        |
    |                               |                           |                        |
    |--BookStep(Step_0)------------>|                           |                        |
    |                               |--BookAction(confirmed)--->|                        |
    |<--BookStepConfirmed-----------|<--BookingConfirmed--------|                        |
```

### 5.2 Dispatching Agent ↔ Planning Agent

**Topics:**
- **Request Offer:** `/DispatchingAgent/{Namespace}/ModuleOffers/{ModuleId}/Request/`
- **Offer Response:** `/DispatchingAgent/{Namespace}/ModuleOffers/{ModuleId}/Response/`
- **Schedule Action:** `/Modules/{ModuleId}/ScheduleAction/`
- **Booking Confirmation:** `/Modules/{ModuleId}/BookingConfirmation/`

**Nachrichtenformat (RequestOffer):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "DispatchingAgent_Line1" } },
    "receiver": { "identification": { "id": "Module1_Planning_Agent" } },
    "type": "request",
    "conversationId": "offer-12345-step0"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "OfferRequest",
      "value": [
        { "idShort": "CapabilityType", "value": "Drilling", "valueType": "xs:string" },
        { "idShort": "EarliestStart", "value": "2025-12-15T10:00:00Z", "valueType": "xs:dateTime" },
        { "idShort": "Deadline", "value": "2025-12-15T14:00:00Z", "valueType": "xs:dateTime" },
        { "idShort": "ProductType", "value": "Cab_A_Blue", "valueType": "xs:string" }
      ]
    }
  ]
}
```

**Nachrichtenformat (Offer Response):**
```json
{
  "frame": {
    "sender": { "identification": { "id": "Module1_Planning_Agent" } },
    "receiver": { "identification": { "id": "DispatchingAgent_Line1" } },
    "type": "consent",  // oder "refuse"
    "conversationId": "offer-12345-step0"
  },
  "interactionElements": [
    {
      "modelType": "SubmodelElementCollection",
      "idShort": "Offer",
      "value": [
        { "idShort": "PlannedStart", "value": "2025-12-15T10:05:00Z", "valueType": "xs:dateTime" },
        { "idShort": "PlannedEnd", "value": "2025-12-15T10:20:00Z", "valueType": "xs:dateTime" },
        { "idShort": "Cost", "value": "100.0", "valueType": "xs:double" },
        { "idShort": "Confidence", "value": "0.95", "valueType": "xs:double" }
      ]
    }
  ]
}
```

---

## 6. Hierarchische Dispatching-Struktur

Bei **mehrfach geschachtelten Namespaces** kommunizieren Dispatching Agents untereinander:

**Beispiel:** Produktagent sendet RequestManufacturingSequence an `/Company/`

```
ProductAgent --> DispatchingAgent(/Company/)
                     |
                     ├-> DispatchingAgent(/Company/Factory1/)
                     |       |
                     |       ├-> DispatchingAgent(/Company/Factory1/Line1/)
                     |       |       ├-> Module1
                     |       |       └-> Module2
                     |       |
                     |       └-> DispatchingAgent(/Company/Factory1/Line2/)
                     |               └-> Module3
                     |
                     └-> DispatchingAgent(/Company/Factory2/)
                             └-> ...
```

**Ablauf:**
1. `/Company/` DispatchingAgent prüft: Kann ich die Anfrage hier beantworten?
2. Falls nicht → **Broadcast ProcessChain** an Child-DispatchingAgents (`/Company/Factory1/`, `/Company/Factory2/`)
3. Child-DispatchingAgents antworten mit ihren ProcessChains (oder refuse)
4. `/Company/` aggregiert Antworten und wählt beste Kombination
5. Finale ManufacturingSequence wird an Produktagent zurückgegeben

**Hierarchische Koordination:**
- **Horizontale Koordination:** Zwischen Modulen innerhalb eines Dispatchers (z.B. Line1)
- **Vertikale Delegation:** Von übergeordnetem Dispatcher zu Sub-Dispatchern
- **Cross-Factory Routing:** Übergeordneter Dispatcher kann Aufträge zwischen Factories koordinieren

---

## 7. Behavior Tree Struktur des Dispatching Agents

### 7.1 Hauptbaum (DispatchingAgent.bt.xml)

```xml
<Root>
  <BehaviorTree name="DispatchingAgent">
    <Parallel name="DispatcherMainLoop" policy="ParallelAll">
      
      <!-- Configuration & Registration Handler -->
      <Sequence name="Configuration">
        <ReadConfig ConfigKey="DispatchingAgent"/>
        <ConnectToMessagingBroker/>
        <SubscribeToRegistrationTopic Namespace="{Namespace}"/>
        <AlwaysSuccess/>
      </Sequence>
      
      <!-- Service Handlers (parallel) -->
      <Parallel name="ServiceHandlers" policy="ParallelAll">
        
        <!-- 1. Handle ProcessChain Requests -->
        <RepeatUntilFailure name="ProcessChainHandler">
          <Sequence>
            <WaitForMessage Topic="/{Namespace}/ProcessChain" Timeout="5000"/>
            <SubTree name="HandleProcessChainRequest"/>
            <Wait DelayMs="100"/>
          </Sequence>
        </RepeatUntilFailure>
        
        <!-- 2. Handle ManufacturingSequence Requests -->
        <RepeatUntilFailure name="MfgSeqHandler">
          <Sequence>
            <WaitForMessage Topic="/DispatchingAgent/{Namespace}/RequestManufacturingSequence/" Timeout="5000"/>
            <SubTree name="HandleManufacturingSequenceRequest"/>
            <Wait DelayMs="100"/>
          </Sequence>
        </RepeatUntilFailure>
        
        <!-- 3. Handle BookStep Requests -->
        <RepeatUntilFailure name="BookStepHandler">
          <Sequence>
            <WaitForMessage Topic="/DispatchingAgent/{Namespace}/BookStep/" Timeout="5000"/>
            <SubTree name="HandleBookStepRequest"/>
            <Wait DelayMs="100"/>
          </Sequence>
        </RepeatUntilFailure>
        
        <!-- 4. Handle TransportPlan Requests -->
        <RepeatUntilFailure name="TransportHandler">
          <Sequence>
            <WaitForMessage Topic="/DispatchingAgent/{Namespace}/RequestTransportPlan/" Timeout="5000"/>
            <SubTree name="HandleTransportPlanRequest"/>
            <Wait DelayMs="100"/>
          </Sequence>
        </RepeatUntilFailure>
        
        <!-- 5. Module Registration Handler -->
        <RepeatUntilFailure name="RegistrationHandler">
          <Sequence>
            <WaitForMessage Topic="/DispatchingAgent/{Namespace}/ModuleRegistration/" Timeout="5000"/>
            <RegisterModule/>
            <FetchCapabilityDescription ModuleId="{ModuleId}" AasId="{AasId}"/>
            <UpdateCapabilityIndex/>
            <Wait DelayMs="100"/>
          </Sequence>
        </RepeatUntilFailure>
        
      </Parallel>
      
      <!-- Monitoring & Health -->
      <RepeatUntilFailure name="HealthMonitor">
        <Sequence>
          <CheckModuleHealth/>
          <CleanupStaleRegistrations Age="300000"/>
          <Wait DelayMs="10000"/>
        </Sequence>
      </RepeatUntilFailure>
      
    </Parallel>
  </BehaviorTree>
</Root>
```

### 7.2 SubTree: HandleProcessChainRequest

```xml
<SubTree name="HandleProcessChainRequest">
  <Sequence name="GenerateProcessChain">
    <ParseProcessChainRequest/>
    <SetBlackboardValue Key="ProcessChainId" Value="{ConversationId}"/>
    
    <ForEachRequiredCapability>
      <Sequence>
        <FindModulesForCapability CapabilityType="{CurrentCapability}"/>
        <Fallback>
          <Sequence name="LocalMatch">
            <CheckLocalCapabilityIndex CapabilityType="{CurrentCapability}"/>
            <AddCandidatesToStep StepId="{CurrentStepId}" Candidates="{LocalCandidates}"/>
          </Sequence>
          <Sequence name="DelegateToChildDispatchers">
            <BroadcastToChildDispatchers Request="ProcessChain" Capability="{CurrentCapability}"/>
            <WaitForChildResponses Timeout="5000"/>
            <AggregateChildCandidates/>
          </Sequence>
        </Fallback>
      </Sequence>
    </ForEachRequiredCapability>
    
    <Fallback>
      <Sequence name="SendConsent">
        <CheckProcessChainComplete/>
        <BuildProcessChainResponse/>
        <SendMessage Topic="/{Namespace}/ProcessChain" Type="consent"/>
        <LogProcessChain Level="INFO"/>
      </Sequence>
      <Sequence name="SendRefuse">
        <BuildRefusalResponse Reason="no-candidates-found"/>
        <SendMessage Topic="/{Namespace}/ProcessChain" Type="refuse"/>
        <LogProcessChain Level="WARN"/>
      </Sequence>
    </Fallback>
  </Sequence>
</SubTree>
```

### 7.3 SubTree: HandleManufacturingSequenceRequest

```xml
<SubTree name="HandleManufacturingSequenceRequest">
  <Sequence name="GenerateManufacturingSequence">
    <ParseManufacturingSequenceRequest/>
    <LoadProcessChainReference/>
    
    <!-- 1. Request Offers from Planning Agents -->
    <ForEachProcessChainStep>
      <Sequence>
        <ForEachCandidateModule>
          <Sequence>
            <SendOfferRequest ModuleId="{CandidateModule}" Capability="{RequiredCapability}"/>
            <SetBlackboardValue Key="offer_request_{CandidateModule}_{StepId}" Value="pending"/>
          </Sequence>
        </ForEachCandidateModule>
      </Sequence>
    </ForEachProcessChainStep>
    
    <!-- 2. Collect Offers -->
    <WaitForOffers Timeout="5000" MinOffers="1"/>
    <AggregateOffers/>
    
    <!-- 3. Routing Algorithm -->
    <ExecuteRoutingAlgorithm Algorithm="MinimizeMakespan"/>
    <SelectBestModuleCombination/>
    
    <!-- 4. Transport Planning -->
    <ForEachConsecutiveStepPair>
      <Sequence>
        <CheckModuleDistance FromModule="{Step[i].Module}" ToModule="{Step[i+1].Module}"/>
        <Fallback>
          <Sequence name="SameModule">
            <CheckSameModule/>
            <AlwaysSuccess/>
          </Sequence>
          <Sequence name="NeighborModules">
            <CheckDirectNeighbor/>
            <AddTransportStep Duration="30s"/>
          </Sequence>
          <Sequence name="DistantModules">
            <FindShortestPath FromModule="{FromModule}" ToModule="{ToModule}"/>
            <AddTransportSteps Path="{ShortestPath}"/>
          </Sequence>
        </Fallback>
      </Sequence>
    </ForEachConsecutiveStepPair>
    
    <!-- 5. Tentative Booking -->
    <ForEachScheduledStep>
      <Sequence>
        <SendScheduleAction ModuleId="{AssignedModule}" BookingStatus="tentative"/>
        <WaitForBookingConfirmation Timeout="3000"/>
        <UpdateStepBookingStatus Status="{ResponseStatus}"/>
      </Sequence>
    </ForEachScheduledStep>
    
    <!-- 6. Response -->
    <Fallback>
      <Sequence name="Success">
        <CheckAllStepsBooked/>
        <BuildManufacturingSequenceResponse/>
        <SendMessage Type="consent"/>
        <LogManufacturingSequence Level="INFO"/>
      </Sequence>
      <Sequence name="Failure">
        <RollbackTentativeBookings/>
        <BuildRefusalResponse Reason="booking-failed"/>
        <SendMessage Type="refuse"/>
        <LogManufacturingSequence Level="ERROR"/>
      </Sequence>
    </Fallback>
  </Sequence>
</SubTree>
```

---

## 8. BT Nodes für Dispatching Agent

### 8.1 Configuration Nodes

- **`SubscribeToRegistrationTopic`**: Abonniert `/DispatchingAgent/{Namespace}/ModuleRegistration/`
- **`RegisterModule`**: Parst ModuleRegistration-Message und speichert in ModuleRegistry
- **`FetchCapabilityDescription`**: Lädt CapabilityDescription aus AAS via `AasSharpClient`
- **`UpdateCapabilityIndex`**: Fügt Modul-Capabilities zum Index hinzu

### 8.2 Planning Nodes

- **`ParseProcessChainRequest`**: Extrahiert RequiredCapabilities aus Nachricht
- **`FindModulesForCapability`**: Sucht Module im CapabilityIndex
- **`CheckLocalCapabilityIndex`**: Prüft ob lokale Module die Capability erfüllen
- **`BroadcastToChildDispatchers`**: Sendet Request an untergeordnete DispatchingAgents
- **`WaitForChildResponses`**: Sammelt Antworten von Child-Dispatchern
- **`AggregateChildCandidates`**: Kombiniert Kandidaten aus Child-Antworten
- **`BuildProcessChainResponse`**: Erstellt ProcessChain-AAS-Objekt

### 8.3 Scheduling Nodes

- **`SendOfferRequest`**: Sendet OfferRequest an Planning Agent eines Moduls
- **`WaitForOffers`**: Sammelt Offer-Responses von Planning Agents
- **`AggregateOffers`**: Speichert Offers in Context
- **`ExecuteRoutingAlgorithm`**: Wählt beste Modul-Kombination (z.B. MinimizeMakespan, MinimizeCost)
- **`SelectBestModuleCombination`**: Setzt `AssignedModule` pro Step

### 8.4 Transport Planning Nodes

- **`CheckModuleDistance`**: Prüft Distanz zwischen zwei Modulen
- **`CheckDirectNeighbor`**: Prüft ob Module direkt benachbart sind
- **`FindShortestPath`**: Dijkstra/A* auf Topologie-Graph
- **`AddTransportStep`**: Fügt Transport-Step zur ManufacturingSequence hinzu
- **`AddTransportSteps`**: Fügt mehrere Transport-Steps (für Pfad) hinzu

### 8.5 Booking Nodes

- **`SendScheduleAction`**: Sendet ScheduleAction (tentative/confirmed) an Planning Agent
- **`WaitForBookingConfirmation`**: Wartet auf Booking-Confirmation von Planning Agent
- **`UpdateStepBookingStatus`**: Aktualisiert BookingStatus im Context
- **`RollbackTentativeBookings`**: Sendet CancelScheduleAction an alle Module mit tentative Bookings
- **`ConfirmBooking`**: Ändert BookingStatus von tentative → confirmed

### 8.6 Messaging Nodes

- **`BuildManufacturingSequenceResponse`**: Erstellt ManufacturingSequence-AAS-Objekt
- **`BuildRefusalResponse`**: Erstellt refuse-Nachricht mit Grund
- **`LogProcessChain`**: Loggt ProcessChain (INFO/WARN)
- **`LogManufacturingSequence`**: Loggt ManufacturingSequence (INFO/ERROR)

### 8.7 Monitoring Nodes

- **`CheckModuleHealth`**: Prüft ob registrierte Module noch alive sind (via HeartbeatMessage)
- **`CleanupStaleRegistrations`**: Entfernt Module die länger als `Age` keine Heartbeat gesendet haben

### 8.8 Iteration Nodes

- **`ForEachRequiredCapability`**: Iteriert über RequiredCapabilities aus Request
- **`ForEachCandidateModule`**: Iteriert über Kandidaten-Module eines Steps
- **`ForEachProcessChainStep`**: Iteriert über Steps der ProcessChain
- **`ForEachScheduledStep`**: Iteriert über Steps der ManufacturingSequence
- **`ForEachConsecutiveStepPair`**: Iteriert über (Step[i], Step[i+1]) Paare

---

## 9. Services und Utilities

### 9.1 DispatchingAgentService

```csharp
public class DispatchingAgentService
{
    private readonly ModuleRegistry _moduleRegistry;
    private readonly CapabilityIndex _capabilityIndex;
    private readonly TopologyGraph _topologyGraph;
    private readonly ILogger _logger;
    
    public void RegisterModule(ModuleInfo moduleInfo)
    {
        _moduleRegistry.Add(moduleInfo.ModuleId, moduleInfo);
        _capabilityIndex.AddModule(moduleInfo.ModuleId, moduleInfo.Capabilities);
        _topologyGraph.AddNode(moduleInfo.ModuleId, moduleInfo.Neighbors);
    }
    
    public List<string> FindModulesForCapability(string capabilityType)
    {
        return _capabilityIndex.FindModulesForCapability(capabilityType);
    }
    
    public async Task<Offer> RequestOfferAsync(string moduleId, OfferRequest request)
    {
        var topic = $"/DispatchingAgent/{Namespace}/ModuleOffers/{moduleId}/Request/";
        var message = BuildOfferRequestMessage(request);
        await _messagingClient.PublishAsync(topic, message);
        
        var responseTopic = $"/DispatchingAgent/{Namespace}/ModuleOffers/{moduleId}/Response/";
        var response = await _messagingClient.WaitForMessageAsync(responseTopic, timeout: 5000);
        return ParseOfferResponse(response);
    }
}
```

### 9.2 RoutingAlgorithm

```csharp
public class RoutingAlgorithm
{
    public ManufacturingSequence MinimizeMakespan(ProcessChain processChain, Dictionary<string, List<Offer>> offers)
    {
        // Greedy: Wähle pro Step das Angebot mit frühestem End-Zeitpunkt
        var sequence = new ManufacturingSequence();
        DateTime currentTime = DateTime.Now;
        
        foreach (var step in processChain.Steps)
        {
            var bestOffer = offers[step.StepId]
                .Where(o => o.PlannedStart >= currentTime)
                .OrderBy(o => o.PlannedEnd)
                .FirstOrDefault();
            
            if (bestOffer == null)
                throw new Exception($"No feasible offer for step {step.StepId}");
            
            sequence.Steps.Add(new ScheduledStep
            {
                StepId = step.StepId,
                AssignedModule = bestOffer.ModuleId,
                PlannedStart = bestOffer.PlannedStart,
                PlannedEnd = bestOffer.PlannedEnd,
                BookingStatus = "tentative"
            });
            
            currentTime = bestOffer.PlannedEnd;
        }
        
        return sequence;
    }
}
```

### 9.3 TopologyGraph

```csharp
public class TopologyGraph
{
    private Dictionary<string, List<string>> _adjacencyList;
    
    public void AddNode(string moduleId, List<string> neighbors)
    {
        _adjacencyList[moduleId] = neighbors;
    }
    
    public List<string> FindShortestPath(string fromModule, string toModule)
    {
        // BFS für kürzesten Pfad
        var queue = new Queue<(string node, List<string> path)>();
        var visited = new HashSet<string>();
        
        queue.Enqueue((fromModule, new List<string> { fromModule }));
        visited.Add(fromModule);
        
        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            
            if (current == toModule)
                return path;
            
            foreach (var neighbor in _adjacencyList[current])
            {
                if (!visited.Contains(neighbor))
                {
                    visited.Add(neighbor);
                    var newPath = new List<string>(path) { neighbor };
                    queue.Enqueue((neighbor, newPath));
                }
            }
        }
        
        throw new Exception($"No path found from {fromModule} to {toModule}");
    }
}
```

---

## 10. Integration mit bestehenden Agenten

### 10.1 Product Agent Änderungen

Produktagent erhält neue Nodes:

- **`ProcessChain`**: Sendet ProcessChain an DispatchingAgent
- **`WaitForProcessChainResponse`**: Wartet auf ProcessChain vom DispatchingAgent
- **`SelectManufacturingOption`**: Wählt beste Option aus ProcessChain (kann mehrere geben)
- **`RequestManufacturingSequence`**: Sendet RequestManufacturingSequence
- **`WaitForManufacturingSequenceResponse`**: Wartet auf terminierte Sequence
- **`AcceptManufacturingSequence`**: Bestätigt Annahme der Sequence
- **`BookNextStep`**: Sendet BookStep für nächsten fälligen Step
- **`MonitorStepExecution`**: Überwacht StepUpdate vom ausführenden Modul

### 10.2 Planning Agent Änderungen

Planning Agent erhält neue Topics und Nodes:

**Topics:**
- Subscribe: `/DispatchingAgent/{Namespace}/ModuleOffers/{ModuleId}/Request/`
- Publish: `/DispatchingAgent/{Namespace}/ModuleOffers/{ModuleId}/Response/`
- Subscribe: `/Modules/{ModuleId}/ScheduleAction/`
- Publish: `/Modules/{ModuleId}/BookingConfirmation/`

**Neue Nodes:**
- **`ReceiveOfferRequest`**: Empfängt OfferRequest vom DispatchingAgent
- **`CalculateOffer`**: Berechnet Angebot (PlannedStart, PlannedEnd, Cost)
- **`SendOffer`**: Sendet Offer zum DispatchingAgent
- **`ReceiveScheduleAction`**: Empfängt ScheduleAction (tentative/confirmed)
- **`UpdateMachineSchedule`**: Fügt Action zum Schedule hinzu oder ändert Status
- **`SendBookingConfirmation`**: Bestätigt oder lehnt Booking ab

### 10.3 Execution Agent (keine Änderungen)

Execution Agent bleibt unverändert – er empfängt weiterhin SkillRequests vom Planning Agent.

---

## 11. Beispiel-Ablauf (End-to-End)

### Szenario: Produktagent bestellt Fertigung eines Produkts

**1. Produktagent fragt ProcessChain an:**
```
ProductHolon_12345 --> DispatchingAgent_Line1: ProcessChain
  RequiredCapabilities: [Drilling, Screwing, Painting]
```

**2. DispatchingAgent prüft CapabilityIndex:**
```
Drilling → [Module1, Module3]
Screwing → [Module2]
Painting → [Module4]
```

**3. DispatchingAgent antwortet mit ProcessChain:**
```
DispatchingAgent_Line1 --> ProductHolon_12345: ProcessChain
  Step_0: Drilling → Candidates: [Module1, Module3]
  Step_1: Screwing → Candidates: [Module2]
  Step_2: Painting → Candidates: [Module4]
```

**4. Produktagent wählt Sequence-Generierung:**
```
ProductHolon_12345 --> DispatchingAgent_Line1: RequestManufacturingSequence
  ProcessChainReference: <ProcessChain>
  Deadline: 2025-12-15T14:00:00Z
```

**5. DispatchingAgent fordert Angebote an:**
```
DispatchingAgent_Line1 --> Module1_Planning: RequestOffer (Drilling, Deadline)
DispatchingAgent_Line1 --> Module3_Planning: RequestOffer (Drilling, Deadline)
DispatchingAgent_Line1 --> Module2_Planning: RequestOffer (Screwing, Deadline)
DispatchingAgent_Line1 --> Module4_Planning: RequestOffer (Painting, Deadline)
```

**6. Planning Agents antworten:**
```
Module1_Planning --> DispatchingAgent_Line1: Offer
  PlannedStart: 10:00, PlannedEnd: 10:15, Cost: 100

Module3_Planning --> DispatchingAgent_Line1: Offer
  PlannedStart: 10:30, PlannedEnd: 10:45, Cost: 80

Module2_Planning --> DispatchingAgent_Line1: Offer
  PlannedStart: 10:20, PlannedEnd: 10:35, Cost: 50

Module4_Planning --> DispatchingAgent_Line1: Offer
  PlannedStart: 10:40, PlannedEnd: 11:00, Cost: 120
```

**7. DispatchingAgent wählt beste Kombination (MinimizeMakespan):**
```
Step_0: Module1 (10:00-10:15)
Transport_0_1: Module1 → Module2 (10:15-10:20)
Step_1: Module2 (10:20-10:35)
Transport_1_2: Module2 → Module4 (10:35-10:40)
Step_2: Module4 (10:40-11:00)
```

**8. DispatchingAgent sendet tentative ScheduleActions:**
```
DispatchingAgent_Line1 --> Module1_Planning: ScheduleAction (Step_0, tentative)
DispatchingAgent_Line1 --> Module2_Planning: ScheduleAction (Step_1, tentative)
DispatchingAgent_Line1 --> Module4_Planning: ScheduleAction (Step_2, tentative)
```

**9. DispatchingAgent antwortet mit ManufacturingSequence:**
```
DispatchingAgent_Line1 --> ProductHolon_12345: ManufacturingSequence
  [Steps mit tentative Bookings]
```

**10. Produktagent akzeptiert:**
```
ProductHolon_12345 --> DispatchingAgent_Line1: AcceptSequence
```

**11. Vor Ausführung: BookStep:**
```
ProductHolon_12345 --> DispatchingAgent_Line1: BookStep (Step_0)
DispatchingAgent_Line1 --> Module1_Planning: ScheduleAction (Step_0, confirmed)
Module1_Planning --> DispatchingAgent_Line1: BookingConfirmed
DispatchingAgent_Line1 --> ProductHolon_12345: BookStepConfirmed
```

**12. Ausführung:**
```
Module1_Planning --> Module1_Execution: SkillRequest (Drilling)
Module1_Execution --> Skill (via OPC UA)
Module1_Execution --> Module1_Planning: SkillResponse (Completed)
Module1_Planning --> ProductHolon_12345: StepUpdate (Step_0, DONE)
```

**13. Produkt-Holon wiederholt BookStep für Step_1, Step_2...**

---

## 12. Offene Fragen und Design-Entscheidungen

### Frage 1: Wie granular ist der Capability-Match?
**Entscheidung:** 
- Minimale Überprüfung im DispatchingAgent (nur CapabilityType)
- Detaillierte Constraint-Prüfung (Material, Tools, etc.) bleibt beim Planning Agent
- Grund: DispatchingAgent soll schnell sein; Planning Agent kennt aktuelle Resource-Zustände besser

### Frage 2: Wie wird mit Offer-Timeouts umgegangen?
**Entscheidung:**
- DispatchingAgent wartet auf Offers mit konfigurierbarem Timeout (default 5s)
- Falls nicht alle Module antworten → arbeite mit verfügbaren Offers
- Falls gar keine Offers → refuse mit Grund "no-feasible-modules"

### Frage 3: Was passiert bei Booking-Konflikten?
**Entscheidung:**
- Planning Agent kann tentative Bookings ablehnen (z.B. wenn Zeitfenster nicht mehr frei)
- DispatchingAgent führt Rollback durch (CancelScheduleAction an alle tentative Bookings)
- Produktagent erhält refuse → kann neu planen oder anderen DispatchingAgent fragen

### Frage 4: Wie werden Transporte koordiniert?
**Entscheidung (Phase 1):**
- DispatchingAgent plant Transporte selbst (einfache Distanz-basierte Dauer-Schätzung)
- Keine explizite Transport-Ressourcen-Modellierung
- **Zukunft:** Separater TransportAgent der via RequestTransportPlan angefragt wird

### Frage 5: Wie erfolgt Load Balancing bei mehreren Kandidaten?
**Entscheidung:**
- RoutingAlgorithm berücksichtigt Offers (PlannedStart, Cost, Confidence)
- Algorithmen: MinimizeMakespan (default), MinimizeCost, LoadBalanced
- Konfigurierbar via config.json → `DispatchingAgent.RoutingAlgorithm`

### Frage 6: Heartbeat/Health-Monitoring der Module?
**Entscheidung:**
- Module senden periodisch HeartbeatMessage (alle 30s) an DispatchingAgent
- DispatchingAgent entfernt Module nach 5 Minuten ohne Heartbeat aus Registry
- Bei Re-Registrierung werden Module wieder aufgenommen

---

## 13. Nächste Schritte (Implementierung)

### Phase 1: Core Infrastructure
1. Datenmodelle (ModuleInfo, ProcessChain, ManufacturingSequence, Offer)
2. Services (DispatchingAgentService, CapabilityIndex, TopologyGraph)
3. MQTT Topic-Struktur etablieren
4. Basis BT Nodes (RegisterModule, FetchCapabilityDescription, UpdateCapabilityIndex)

### Phase 2: ProcessChain Service
1. Nodes: ParseProcessChainRequest, FindModulesForCapability, BuildProcessChainResponse
2. SubTree: HandleProcessChainRequest
3. Test: Produktagent → DispatchingAgent → ProcessChain Response

### Phase 3: ManufacturingSequence Service
1. Nodes: SendOfferRequest, WaitForOffers, ExecuteRoutingAlgorithm, SelectBestModuleCombination
2. Nodes: AddTransportStep, FindShortestPath
3. Nodes: SendScheduleAction, WaitForBookingConfirmation, RollbackTentativeBookings
4. SubTree: HandleManufacturingSequenceRequest
5. Test: End-to-End mit Planning Agent Integration

### Phase 4: BookStep Service
1. Nodes: ConfirmBooking
2. SubTree: HandleBookStepRequest
3. Test: BookStep → confirmed Booking im MachineSchedule

### Phase 5: TransportPlan Service
1. SubTree: HandleTransportPlanRequest
2. Test: Standalone Transport-Anfragen

### Phase 6: Hierarchical Dispatching
1. Nodes: BroadcastToChildDispatchers, WaitForChildResponses, AggregateChildCandidates
2. Test: Multi-Level Dispatcher-Hierarchie

### Phase 7: Monitoring & Robustness
1. Nodes: CheckModuleHealth, CleanupStaleRegistrations
2. HeartbeatMessage-Integration
3. Error Handling & Recovery

---

## 14. Änderungen an bestehenden Dokumenten

### 14.1 Ergänzungen in Konzept.md

**Neuer Abschnitt:**
```markdown
## **8. Dispatching Agent**

Der Dispatching Agent koordiniert mehrere Produktionsmodule unter einem Namespace und bietet Services für:
- **ProcessChain-Generierung:** Findet Modul-Kandidaten für Required Capabilities
- **ManufacturingSequence-Planung:** Erstellt terminierte Fertigungssequenzen mit Transporten
- **Booking:** Überführt tentative Bookings in confirmed Bookings
- **Transportplanung:** Koordiniert Transporte zwischen Modulen

Siehe `DispatchingAgent.md` für Details.
```

### 14.2 Neue Nodes in specs.json

```json
{
  "DispatchingNodes": {
    "RegisterModule": "Registers a module with the DispatchingAgent",
    "FetchCapabilityDescription": "Fetches CapabilityDescription from AAS",
    "UpdateCapabilityIndex": "Updates the internal capability index",
    "ParseProcessChainRequest": "Parses incoming ProcessChain request",
    "FindModulesForCapability": "Finds modules that support a capability",
    "BuildProcessChainResponse": "Builds ProcessChain response message",
    "SendOfferRequest": "Sends offer request to Planning Agent",
    "WaitForOffers": "Collects offers from Planning Agents",
    "ExecuteRoutingAlgorithm": "Executes routing algorithm (e.g., MinimizeMakespan)",
    "SelectBestModuleCombination": "Selects best module combination from offers",
    "AddTransportStep": "Adds transport step to manufacturing sequence",
    "FindShortestPath": "Finds shortest path in topology graph",
    "SendScheduleAction": "Sends ScheduleAction to Planning Agent",
    "WaitForBookingConfirmation": "Waits for booking confirmation",
    "RollbackTentativeBookings": "Cancels all tentative bookings",
    "ConfirmBooking": "Changes booking status from tentative to confirmed",
    "CheckModuleHealth": "Checks health of registered modules",
    "CleanupStaleRegistrations": "Removes modules without recent heartbeat"
  }
}
```

---

## 15. Zusammenfassung

Der **Dispatching Agent** ist ein zentraler Koordinator im MAS-BT-System, der:

1. **Abstrahiert:** Module unter einem Namespace gruppiert
2. **Vermittelt:** Zwischen Produktagent und Planning Agents
3. **Plant:** ProcessChains und ManufacturingSequences
4. **Koordiniert:** Transporte und Bookings
5. **Skaliert:** Hierarchische Strukturen über mehrere Ebenen

Die Implementierung folgt dem bestehenden Pattern (BT Nodes, Services, MQTT-Messaging, AAS-Datenmodelle) und integriert nahtlos mit:
- **Produktagent:** Empfängt Produktions-Requests
- **Planning Agent:** Empfängt Offer-Requests, sendet Offers
- **Execution Agent:** Keine Änderungen nötig

Die Architektur ist modular, erweiterbar und unterstützt komplexe Szenarien wie:
- Multi-Factory Routing
- Dynamische Umplanung bei Störungen
- Load Balancing über mehrere Ressourcen
- Transport-Koordination

**Status:** Konzept vollständig dokumentiert, bereit für Implementierung.

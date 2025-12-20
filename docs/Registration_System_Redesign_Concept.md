# Konzept: Überarbeitung des Registrierungsmechanismus

**Datum:** 2025-12-20  
**Status:** Entwurf  
**Autor:** System-Analyse basierend auf Code-Review und Runtime-Logs

---

## 1. Problemstellung

### 1.1 Beobachtete Symptome
- **Capabilities kommen leer an**: Dispatcher registriert Sub-Agents (P100, P101, P102, TransportManager), aber deren `Capabilities`-Array ist leer
- **Inkonsistente Hierarchie-Kommunikation**: Registrierungsnachrichten zeigen teils falsche Parent-Child-Beziehungen (z.B. `_PHUKET` als Subagent des Dispatchers statt umgekehrt)
- **Topic-Mismatch**: Module registrieren sich möglicherweise auf Topics, die der Dispatcher nicht subscribed

### 1.2 Analysierte Root-Causes

#### A. **Capabilities werden nicht geladen/extrahiert** (wahrscheinlichste Ursache)
- **Planning-Holons** laden `CapabilityDescriptionSubmodel` via `LoadCapabilityDescriptionSubmodelNode`
- `ExtractCapabilityNames` muss erfolgreich sein, damit `Capabilities` im Context landen
- **Problem**: Wenn AAS-Shell nicht geladen werden kann (falsche AgentId, Repository-Fehler), bleiben Capabilities leer
- **Evidence**: Planning-Agents nutzen `ModuleName` (z.B. "ScrewingStation") vs. `ModuleId` (z.B. "P100") – Shell-Lookup kann fehlschlagen

#### B. **Topic-Subscription-Lücken beim Dispatcher**
```
Aktuell subscribed der Dispatcher:
- /{ns}/ManufacturingSequence/Request
- /{ns}/register                     ← namespace-level registration
- /{ns}/+/Inventory                  ← wildcard inventory

NICHT subscribed:
- /{ns}/+/register                   ← per-module registrations
- /{ns}/DispatchingAgent/register    ← direct child registrations
```
**Folge**: Module, die sich bei `ParentAgent=DispatchingAgent` registrieren, publishen auf `/{ns}/DispatchingAgent/register` – Dispatcher empfängt diese nicht!

#### C. **Timing & Sequenzprobleme**
- `ModuleHolon.bt.xml` ruft `SpawnSubHolons` → `WaitForRegistration` → `RegisterAgent`
- **Problem**: `WaitForRegistration` kennt nur Config-Dateinamen (z.B. `["P100_Execution_agent","P100_Planning_agent"]`), nicht die runtime AgentIds
- `ExpectedAgents` ist absichtlich leer → Node wartet nur auf Anzahl, nicht auf spezifische IDs
- **Risiko**: Module registrieren sich beim Dispatcher bevor ihre Sub-Holons Capabilities geladen haben

#### D. **Parsing-Fehler (teilweise behoben)**
- `RegisterMessage.FromSubmodelElementCollection()` verwendete `.Value?.Value?.ToObject<string>()`
- **Fix bereits implementiert**: Umstellung auf `IValue.Value?.ToString()` mit Fallback
- `DispatchingModuleInfo.FromMessage()` hat ähnliche Fallback-Logik erhalten

---

## 2. Ist-Zustand: Registrierungsfluss

### 2.1 Hierarchie (Soll-Design)
```
NamespaceHolon (_PHUKET)
├── ManufacturingDispatcher_phuket (Dispatching)
│   └── registriert sich bei: /{ns}/register
├── TransportManager_phuket
│   └── registriert sich bei: /{ns}/register
├── SimilarityAgent (S1)
│   └── registriert sich bei: /{ns}/register
└── Module (P100, P101, P102)
    ├── registriert sich bei: /{ns}/DispatchingAgent/register (implizit)
    └── Sub-Holons:
        ├── P100_Planning (PlanningHolon)
        │   ├── lädt CapabilityDescriptionSubmodel
        │   ├── extrahiert Capabilities → Context
        │   └── registriert sich bei: /{ns}/P100/register
        └── P100_Execution (ExecutionHolon)
            └── registriert sich bei: /{ns}/P100/register
```

### 2.2 Aktueller Ablauf (vereinfacht)

**Planning-Agent Startup (P100_Planning)**:
1. `ReadConfig` → `config.Agent.ModuleName = "ScrewingStation"`
2. `ReadShell` → sucht Shell mit `AgentId = "ScrewingStation"` oder `"P100"`
3. `LoadCapabilityDescriptionSubmodel` → lädt Submodel via Shell-Referenzen
4. `ExtractCapabilityNames` → liest Container-Namen aus `CapabilitySet` → `Context["Capabilities"]`
5. `RegisterAgent` → baut `RegisterMessage(agentId, subagents, capabilities)` und published auf `/{ns}/P100/register`

**ModuleHolon Startup (P100)**:
1. `SpawnSubHolons` → startet P100_Planning und P100_Execution in neuen Prozessen
2. `WaitForRegistration` → wartet auf 2 Registrierungen (Timeout 15s)
3. Empfängt Planning- und Execution-Registrierungen (inkl. Capabilities)
4. `RegisterAgent` → aggregiert Sub-Holon-Capabilities → published auf `/{ns}/DispatchingAgent/register` (oder implizit `/{ns}/register`)

**Dispatcher Startup**:
1. `InitializeAgentState` → erstellt `DispatchingState` (leere Modul-Liste)
2. `SubscribeAgentTopics` → subscribed Topics (siehe oben – **fehlt `/{ns}/+/register`**)
3. **Parallel Loop "RegistrationLoop"**:
   - `WaitForMessage` (ExpectedTypes: "registerMessage,moduleRegistration")
   - `HandleRegistration` → ruft `DispatchingModuleInfo.FromMessage()` → upserted `DispatchingState`
   - Dispatcher sammelt Module und deren Capabilities
4. **Parallel Loop "RegistrationHeartbeat"**:
   - `RegisterAgent` → aggregiert alle Module-Capabilities aus `DispatchingState` → published auf `/{ns}/register`

**Namespace Startup**:
1. Spawnt Dispatcher, TransportManager, S1
2. Wartet auf deren Registrierungen
3. Empfängt Dispatcher-Registration mit aggregierten Capabilities

---

## 3. Soll-Zustand: Gewünschte Eigenschaften

### 3.1 Funktionale Anforderungen
1. **Vollständige Capability-Propagation**: Capabilities müssen von Planning → Module → Dispatcher → Namespace fließen
2. **Korrekte Parent-Child-Topics**: Jeder Agent registriert sich beim richtigen Parent
3. **Zeitliche Konsistenz**: Aggregation erfolgt erst nach Empfang der Sub-Agent-Daten
4. **Idempotenz**: Wiederholte Registrierungen (Heartbeat) überschreiben alte Daten korrekt
5. **Fehlertoleranz**: Partielle Registrierungen (z.B. nur 1 von 2 Planning-Agents) werden akzeptiert, aber geloggt

### 3.2 Nicht-funktionale Anforderungen
1. **Beobachtbarkeit**: Jede Registrierung loggt Sender, Empfänger, Anzahl Capabilities
2. **Debuggability**: Bei leeren Capabilities wird Root-Cause geloggt (Shell fehlt? Submodel fehlt? ExtractCapabilities fehlgeschlagen?)
3. **Performance**: Heartbeat-Registrierungen dürfen Broker nicht überlasten (aktuell: 1-5s Intervall)

---

## 4. Lösungsansätze (priorisiert)

### Priorität 1: Topic-Subscription Fix (Quick Win, hohes Impact)

**Problem**: Dispatcher subscribed nicht auf `/{ns}/+/register` → verpasst Modul-Registrierungen

**Lösung**:
```csharp
// In SubscribeAgentTopicsNode.cs, Dispatching-Branch:
var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    $"/{ns}/ManufacturingSequence/Request",
    $"/{ns}/BookStep/Request",
    $"/{ns}/TransportPlan/Request",
    $"/{ns}/ManufacturingSequence/Response",
    $"/{ns}/Planning/OfferedCapability/Response",
    
    // Registration topics:
    $"/{ns}/register",                    // Namespace-level (Dispatcher → Namespace)
    $"/{ns}/+/register",                  // ← NEU: Alle Module/Sub-Agents → Dispatcher
    $"/{ns}/DispatchingAgent/register",   // ← Optional: explizit für Rückwärtskompatibilität
    
    // Inventory:
    $"/{ns}/+/Inventory"
};
```

**Impact**: Dispatcher empfängt jetzt **alle** Modul-Registrierungen (inkl. Planning/Execution Sub-Holons)

**Risiko**: Minimal – MQTT-Wildcard-Subscriptions sind Standard

---

### Priorität 2: Capability-Loading Robustheit (Medium Impact, Debugging-Fokus)

**Problem**: Planning-Agents scheitern beim Shell-Laden (falsche AgentId) → Capabilities bleiben leer

**Lösung A – Bessere Fallback-Strategie**:
```xml
<!-- In PlanningAgent.bt.xml -->
<Fallback name="LoadModuleShell">
  <ReadShell name="LoadByModuleName" AgentId="{config.Agent.ModuleName}" />
  <ReadShell name="LoadByModuleId" AgentId="{config.Agent.ModuleId}" />
  <ReadShell name="LoadByAgentId" AgentId="{config.Agent.AgentId}" />
</Fallback>
```

**Lösung B – Explizites Error-Logging**:
```csharp
// In ExtractCapabilityNamesNode.cs
if (submodel == null)
{
    Logger.LogError("ExtractCapabilities: CapabilityDescriptionSubmodel not loaded. " +
                   "Possible causes: Shell not found, Submodel missing in repository, or LoadCapabilityDescriptionSubmodel failed.");
    // Prüfe, ob Shell überhaupt geladen wurde:
    var shell = Context.Get<IAssetAdministrationShell>("AAS.Shell");
    if (shell == null)
    {
        Logger.LogError("  → Root cause: AAS Shell not present in context. Check ReadShell node.");
    }
    return Task.FromResult(NodeStatus.Failure);
}

if (capabilities.Count == 0)
{
    Logger.LogWarning("ExtractCapabilities: Loaded CapabilityDescriptionSubmodel, but CapabilitySet is empty. " +
                     "Submodel IdShort: {IdShort}, Identifier: {Id}",
                     submodel.IdShort, submodel.Id?.Id);
}
```

**Lösung C – Config-Validation**:
- Füge Check in `ReadConfig` hinzu: Wenn `Agent.Role == "PlanningHolon"`, müssen `Agent.ModuleName` ODER `Agent.ModuleId` gesetzt sein
- Fehlende Config wirft Startup-Fehler (fail-fast)

---

### Priorität 3: Aggregation-Timing Fix (Medium Impact, Refactoring)

**Problem**: ModuleHolon registriert sich möglicherweise bevor Sub-Holons ihre Capabilities geladen haben

**Lösung A – Explizite Capability-Checks in WaitForRegistration**:
```csharp
// In WaitForRegistrationNode.cs, nach Empfang:
var info = DispatchingModuleInfo.FromMessage(msg);
if (info.Capabilities.Count == 0)
{
    Logger.LogWarning("WaitForRegistration: Received registration from {Id} with ZERO capabilities. " +
                     "This may indicate that the agent has not yet loaded its AAS submodels.",
                     info.ModuleId);
}
```

**Lösung B – Retry-Strategie für leere Capabilities**:
```csharp
// In RegisterAgentNode.cs, vor PublishAsync:
if (capabilities.Count == 0 && RoleLooksLikePlanning(role))
{
    Logger.LogWarning("RegisterAgent: Planning holon {AgentId} has no capabilities. " +
                     "Deferring registration for 2s to allow AAS loading...",
                     registrationAgentId);
    await Task.Delay(2000).ConfigureAwait(false);
    // Retry capability extraction einmal
    capabilities = ExtractCapabilities();
}
```

**Lösung C – Zwei-Phasen-Registrierung** (Aufwändiger):
1. **Phase 1 – Initial Registration**: Agent sendet `RegisterMessage` mit `agentId` und `subagents`, aber leeren Capabilities
2. **Phase 2 – Capability Update**: Sobald Capabilities geladen, sendet Agent `capabilitiesUpdate` (neuer Message-Type)
3. Dispatcher merged beide Nachrichten

**Empfehlung**: Lösung A + B (Logging + kurzer Retry) – C ist zu komplex für MVP

---

### Priorität 4: Parsing-Robustness (bereits teilweise implementiert)

**Status**: `RegisterMessage.FromSubmodelElementCollection()` und `DispatchingModuleInfo.FromMessage()` verwenden jetzt `IValue.Value?.ToString()` statt `.ToObject<string>()`

**Verbleibende Tasks**:
- Unit-Tests für Parsing-Logik mit realen MQTT-Payloads
- Logging bei Parsing-Fehlern: "Failed to extract Capabilities from RegisterMessage collection: {Exception}"

---

### Priorität 5: Struktur-Vereinfachung (Langfristig, Breaking Changes)

**Überlegung**: Aktuell gibt es mehrere Registrierungs-Mechanismen:
- `RegisterMessage` (AAS-Sharp typisiert)
- `DispatchingModuleInfo` (MAS-BT intern)
- `HandleRegistrationNode` → upsert zu `DispatchingState`
- `SubscribeAgentTopics` mit inline Message-Handler

**Vorschlag – Zentralisierte Registrierungs-API**:
```csharp
public interface IRegistrationService
{
    Task RegisterAsync(string agentId, string parentAgent, List<string> capabilities);
    Task<RegistrationSnapshot> GetHierarchySnapshot();
    event EventHandler<AgentRegisteredEventArgs> AgentRegistered;
}
```

**Vorteile**:
- Einheitliche Registrierungs-Logik
- Einfacher zu testen (Mock IRegistrationService)
- Reduziert Duplikation (aktuell: `HandleRegistration`, `SubscribeAgentTopics` inline-handler, `WaitForRegistration`)

**Nachteil**: Erfordert Refactoring aller BT-Nodes → nur für Langfrist-Roadmap

---

## 5. Implementierungsplan (3-Stufen-Rollout)

### Stufe 1: Sofort-Fixes (1-2h Aufwand)
1. **Topic-Subscription erweitern** (Priorität 1)
   - File: `Nodes/Common/SubscribeAgentTopicsNode.cs`
   - Change: Add `$"/{ns}/+/register"` zu Dispatcher-Topics
2. **Logging verbessern** (Priorität 2)
   - Files: `ExtractCapabilityNamesNode.cs`, `RegisterAgentNode.cs`, `HandleRegistrationNode.cs`
   - Change: Log bei leeren Capabilities mit Root-Cause-Hints
3. **Build & Test**
   - Run: `dotnet build && dotnet test MAS-BT/MAS-BT.csproj --filter "Registration"`
4. **Runtime-Verification**
   - Start: `dotnet run -- phuket_full_system --spawn-terminal`
   - Check: Dispatcher-Log zeigt "registered/updated sub-agent P100 with capabilities: [Assemble, Transport, ...]"

**Akzeptanzkriterium**: Dispatcher-Registrierung zeigt nicht-leere Capabilities-Arrays für mindestens 1 Modul

---

### Stufe 2: Robustness-Verbesserungen (1 Tag Aufwand)
1. **Shell-Loading Fallback** (Priorität 2, Lösung A)
   - File: `Trees/PlanningAgent.bt.xml`
   - Change: Dreifacher Fallback (ModuleName → ModuleId → AgentId)
2. **Capability-Retry** (Priorität 3, Lösung B)
   - File: `Nodes/Common/RegisterAgentNode.cs`
   - Change: 2s Delay + einmaliger Retry bei leeren Capabilities
3. **Config-Validation** (Priorität 2, Lösung C)
   - File: `Nodes/Configuration/ReadConfigNode.cs` (optional neuer Validation-Node)
   - Change: Prüfe Pflichtfelder für Planning/Execution/ModuleHolon-Roles
4. **Integration-Tests**
   - File: `tests/RegistrationIntegrationTests.cs`
   - Add: Test-Case `DispatcherAggregatesModuleCapabilities_WhenPlanningAgentsRegister`

**Akzeptanzkriterium**: Alle Module (P100-P106) erscheinen mit vollständigen Capabilities beim Dispatcher

---

### Stufe 3: Architektur-Cleanup (Optional, 2-3 Tage)
1. **Registration-Service Interface** (Priorität 5)
   - New File: `Services/IRegistrationService.cs`, `Services/RegistrationService.cs`
   - Refactor: `HandleRegistration`, `WaitForRegistration`, `SubscribeAgentTopics` nutzen Service
2. **Event-Driven Updates**
   - Replace: Polling-basierte `WaitForRegistration` → Event-Subscription
3. **Monitoring & Metrics**
   - Add: Prometheus-Exporter für Registrierungs-Status (anzahl agents, capabilities pro agent, last-seen timestamps)
4. **Documentation**
   - Update: `MAS-BT/docs/Registration_Flow.md` mit Sequenzdiagramm

**Akzeptanzkriterium**: Alle Tests grün, Code-Coverage für Registration-Logic >80%, kein Duplikat-Code

---

## 6. Risiko-Analyse & Mitigation

| Risiko | Wahrscheinlichkeit | Impact | Mitigation |
|--------|-------------------|--------|-----------|
| Topic-Wildcard verursacht Message-Flut | Niedrig | Hoch | Rate-Limiting im Broker; Dispatcher filtert in `OnMessage` |
| Retry-Logik verzögert Startup | Mittel | Niedrig | Max 1 Retry (2s); asynchron während Heartbeat |
| Shell-Loading schlägt für alle Module fehl | Hoch | Kritisch | Fallback auf Mock-Capabilities (Config); Alert bei 0 Capabilities |
| Registrierungs-Service einführt Breaking Changes | Niedrig | Hoch | Feature-Flag; schrittweise Migration pro BT |

---

## 7. Metriken & Erfolgskriterien

### KPIs nach Implementierung
1. **Capability-Vollständigkeit**: ≥95% der Module haben >0 Capabilities beim Dispatcher
2. **Registrierungs-Latenz**: Zeit von Sub-Holon-Start bis Dispatcher-Aggregation <5s
3. **Fehlschläge**: <1% Registration-Failures in Produktion (gemessen über 24h)
4. **Log-Klarheit**: Bei Registrierungs-Fehler kann Ursache in <2min identifiziert werden (durch Logs allein)

### Monitoring-Dashboard (Optional)
- Grafana-Panel: "Registered Agents" (Zeitreihe: Anzahl Agents pro Namespace)
- Alert: "Dispatcher hat 0 Capabilities" → Slack/Email

---

## 8. Offene Fragen & Diskussionspunkte

1. **ParentAgent-Konvention**: Sollten ModuleHolons sich bei `DispatchingAgent` oder bei `Namespace` registrieren?
   - **Aktuell**: Implizit `DispatchingAgent` (via `RegisterAgent` Fallback-Logik)
   - **Vorschlag**: Explizit in Config setzen: `"ParentAgent": "ManufacturingDispatcher_phuket"`

2. **Sub-Holon Runtime IDs**: Planning/Execution Agents haben Runtime-ID `P100_PlanningHolon`, aber Config-Datei heißt `P100_Planning_agent.json`
   - Führt zu Mismatch bei `WaitForRegistration` (erwartet `P100_Planning_agent`, empfängt `P100_PlanningHolon`)
   - **Vorschlag**: Normalisiere AgentId-Generation in `RegisterAgent.ResolveRegistrationAgentId()`

3. **Heartbeat-Frequenz**: Dispatcher re-registriert alle 5s beim Namespace → erzeugt viel Broker-Traffic
   - **Vorschlag**: Erhöhe auf 30s (oder event-driven: nur bei Änderung der Capabilities)

4. **Neo4j-Sync**: `SyncAgentToNeo4j` läuft nach jeder Registrierung → möglicherweise zu oft
   - **Vorschlag**: Batch-Updates (z.B. alle 10s) oder nur bei Material-Änderungen

---

## 9. Nächste Schritte (Entscheidung erforderlich)

**Option A – Minimale Intervention** (Empfohlen für MVP):
- Implementiere nur **Stufe 1** (Topic-Subscription + Logging)
- Validiere via Runtime-Test (1 Stunde)
- Entscheide nach Ergebnissen über Stufe 2

**Option B – Vollständige Robustness**:
- Implementiere **Stufe 1 + Stufe 2** (2 Tage)
- Umfassende Integration-Tests
- Deployment-Ready für Produktion

**Option C – Langfrist-Refactoring**:
- **Alle 3 Stufen** (1 Woche)
- Breaking Changes akzeptiert
- Neue Registration-API als Foundation für zukünftige Features (z.B. Dynamic Agent Discovery)

---

## Anhang A: Referenzen

- **Relevante Code-Files**:
  - `Nodes/Common/RegisterAgentNode.cs` – Registrierungs-Logik
  - `Nodes/Common/HandleRegistrationNode.cs` – Empfang & Upsert
  - `Nodes/Common/SubscribeAgentTopicsNode.cs` – Topic-Subscriptions
  - `Nodes/Common/WaitForRegistrationNode.cs` – Synchronisations-Node
  - `Models/Messages/RegisterMessage.cs` (AAS-Sharp) – Typisierte Message
  - `Nodes/Dispatching/DispatchingModels.cs` – Interne Modul-Repräsentation

- **Konfiguration**:
  - `configs/specific_configs/NamespaceHolon/NamespaceHolon.json`
  - `configs/specific_configs/NamespaceHolon/ManufacturingDispatcher.json`
  - `configs/specific_configs/Module_configs/P100/*.json`

- **Behavior Trees**:
  - `Trees/NamespaceHolon.bt.xml`
  - `Trees/ManufacturingDispatcher.bt.xml`
  - `Trees/ModuleHolon.bt.xml`
  - `Trees/PlanningAgent.bt.xml`

---

**Ende des Konzepts**

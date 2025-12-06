# ToDo-Liste: Execution Agent Basis-Implementierung
## Update: 

Was ich aktualisieren sollte:
✅ Phase 3: Messaging - Teilweise erledigt
Ich sollte dokumentieren, dass wir gerade:

✅ ModuleState.cs im AAS-Sharp-Client erstellt haben

Ersetzt manuelle Property-Erstellung in SendStateMessageNode
Mit Gettern/Settern und korrekter ValueType-Serialisierung
✅ Action.cs erweitert haben mit:

GetFinalResultData() - gibt FinalResultData zurück
GetInputParameters() - gibt InputParameters zurück
GetActionTitle(), GetMachineName() - Getter-Methoden
✅ SendStateMessageNode refaktoriert

Verwendet jetzt ModuleState aus AAS-Sharp-Client
Keine manuelle Property-Erstellung mehr
✅ SendSkillResponseNode refaktoriert

Verwendet jetzt Action aus Context
Keine manuelle Property-Erstellung mehr
Action enthält alle Properties mit korrekten Values
⚠️ Problem identifiziert: Properties haben keine Values in MQTT Messages

Root Cause: ValueType Property muss via Reflection gesetzt werden
Lösung: I40MessageBuilder.CreateStringProperty() / CreateBooleanProperty() Helper-Methoden erstellt
Status: Kompiliert, aber noch nicht getestet ob Values jetzt korrekt serialisiert werden
## 🚨 KRITISCHE BUGS - SOFORT FIXEN (Phase 3.5)

### 🐛 Bug #1: Lock-Verlust wird nicht recovered
**Problem:** 
- Operator überschreibt Lock manuell im OPC UA
- CheckLockStatus erkennt Lock-Verlust (returns Failure)
- Tree läuft aber weiter ohne Re-Lock (800+ Ticks, 87+ Sekunden)
- Keine automatische Recovery

**Lösung:**
- [ ] **ContinuousMonitoringNode** - Parallel Monitor für Lock + Startup Status
  - Läuft parallel zum Haupt-Tree
  - Bei Lock-Verlust → Trigger Recovery Sequence
  - Bei Startup Halted → Trigger Restart Sequence
  
- [ ] **RecoverySequence für Lock-Verlust:**
  1. Detect Lock Lost (CheckLockStatus fails)
  2. Abort All Running Skills (HaltAllSkills)
  3. Re-Lock Module (LockResource mit Retry)
  4. Restart StartupSkill (ExecuteSkill StartupSkill)
  5. Wait for Startup Running (WaitForSkillState Running)
  6. Resume Main Tree

- [ ] **Tree Pattern: Parallel Monitoring**
  ```xml
  <Parallel name="ExecuteWithMonitoring">
    <!-- Main Execution Branch -->
    <Sequence name="MainExecution">
      <ExecuteSkill .../>
    </Sequence>
    
    <!-- Continuous Monitoring Branch -->
    <RepeatUntilFailure name="ContinuousMonitor">
      <Sequence name="CheckHealthSequence">
        <CheckLockStatus ModuleName="ScrewingStation"/>
        <CheckStartupSkillStatus ModuleName="ScrewingStation"/>
        <Wait DelayMs="1000"/>
      </Sequence>
    </RepeatUntilFailure>
  </Parallel>
  ```

### 🐛 Bug #2: StartupSkill Halted wird nicht restarted
**Problem:**
- StartupSkill geht auf Halted (z.B. durch Operator Reset)
- CheckStartupSkillStatus erkennt Halted-State
- Kein automatischer Restart

**Lösung:**
- [ ] **EnsureStartupRunning Node** - Smart Restart Logic
  - Prüft StartupSkill State
  - Falls Halted → Reset → Start → Wait for Running
  - Falls Running → Success (idempotent)
  - Wird vor jedem Skill-Execution gecallt

- [ ] **Integration in ExecuteSkill:**
  ```xml
  <Sequence name="ExecuteSkillWithStartupCheck">
    <EnsureStartupRunning ModuleName="ScrewingStation"/>
    <ExecuteSkill SkillName="Screw" .../>
  </Sequence>
  ```

### 🐛 Bug #3: Tree läuft endlos nach Lock-Verlust
**Problem:**
- Nach Lock-Verlust läuft Tree weiter (Tick #800+)
- Keine Timeout-Logic
- Keine Failure Propagation

**Lösung:**
- [ ] **Timeout für Lock-Check Sequence**
  - Wrapping mit Timeout Node
  - Max 5 Sekunden für Lock-Check
  - Bei Timeout → Trigger Recovery

- [ ] **Failure Propagation Fix:**
  - CheckLockStatus Failure sollte Sequence abbrechen
  - Statt Sequence → Fallback mit Recovery Branch

### 🐛 Bug #4: CheckLockedStateNode ExpectLocked=true trotz Lock-Verlust
**Problem:**
- CheckLockedStateNode hat `ExpectLocked` Parameter
- Im Tree überall `ExpectLocked="true"` (implizit)
- Bei Lock-Verlust sollte aber Failure zurückgegeben werden

**Analyse:**
- CheckLockedStateNode.cs Zeile 44: `bool matches = (isLocked == ExpectLocked);`
- Wenn isLocked=false, ExpectLocked=true → matches=false → Failure ✅
- **Das ist korrekt!** Bug liegt nicht hier.

**Root Cause:**
- Tree verwendet `RetryUntilSuccess` für Lock-Checks
- Das überschreibt Failures und retried endlos
- **Lösung:** RetryUntilSuccess durch Fallback mit Recovery ersetzen

---

## 🎉 ABGESCHLOSSEN

### ✅ Phase 0: Infrastructure & Cleanup
- [x] **MqttLogger implementiert** - Automatisches Logging aller Nodes via MQTT
- [x] **Trees bereinigt** - 39 `SendLogMessage` Nodes entfernt (53% kleiner)

### ✅ Phase 1: Core Monitoring Nodes (FERTIG) ✨
- [x] **CheckReadyState** - Prüft ob Modul bereit ist
- [x] **CheckErrorState** - Prüft auf Fehler im Modul
- [x] **CheckLockedState** - Erweiterte Lock-Prüfung
- [x] **MonitoringSkill** - Liest Skill State + Monitoring Variables

### ✅ Phase 2: Skill Control Nodes (FERTIG) ✨
- [x] **WaitForSkillState** - Wartet auf spezifischen Skill-Zustand (Polling-basiert)
- [x] **AbortSkill** - Bricht laufenden Skill ab (Halt + Warten auf Halted)
- [x] **PauseSkill** - Pausiert Skill (Suspended State)
- [x] **ResumeSkill** - Setzt pausierten Skill fort
- [x] **RetrySkill** - Wiederholt fehlgeschlagenen Skill mit Exponential Backoff

**Dokumentation:** ✅ MONITORING_AND_SKILL_NODES.md erstellt

---

## 🚀 Priorität 1: Recovery & Monitoring Logic (JETZT - Phase 3.5)

### Recovery Nodes (KRITISCH)

- [ ] **HaltAllSkillsNode** - Haltet alle laufenden Skills
  - Iteriert über alle Skills im Module.SkillSet
  - Ruft AbortSkill für jeden Skill auf
  - Wartet bis alle Halted sind
  - Returns: Success wenn alle Halted

- [ ] **EnsureStartupRunningNode** - Garantiert StartupSkill Running
  - Parameter: ModuleName
  - Logic:
    1. Get StartupSkill State
    2. If Running → Success (idempotent)
    3. If Halted/Completed → Reset → Start → Wait Running
    4. If Ready → Start → Wait Running
    5. Timeout: 60 Sekunden
  - Returns: Success wenn Running, Failure bei Timeout

- [ ] **EnsureModuleLockedNode** - Garantiert Module Lock
  - Parameter: ModuleName, ResourceId
  - Logic:
    1. Check IsLockedByUs
    2. If Locked → Success (idempotent)
    3. If Not Locked → LockResource mit Retry (3x)
    4. Verify Lock nach jedem Versuch
  - Returns: Success wenn Locked, Failure nach 3 Retries

- [ ] **RecoverySequenceNode** - Orchestriert komplette Recovery
  - Parameter: ModuleName
  - Logic:
    1. HaltAllSkills
    2. EnsureModuleLocked
    3. EnsureStartupRunning
    4. Set Context "recoveryCompleted" = true
  - Returns: Success wenn Recovery erfolgreich

### Monitoring Nodes (KRITISCH)

- [ ] **ContinuousHealthCheckNode** - Parallel Monitor
  - Läuft in Parallel Branch
  - Prüft alle 1-2 Sekunden:
    - Lock Status (CheckLockStatus)
    - Startup Status (CheckStartupSkillStatus)
    - Error State (CheckErrorState)
  - Bei Failure → Set Context "healthCheckFailed" = true
  - Returns: Running (endlos) oder Failure bei kritischem Fehler

- [ ] **MonitorAndRecoverNode** - Kombiniert Monitor + Recovery
  - Wrapper Node für Skill Execution
  - Pattern: Parallel mit Main + Monitor Branch
  - Bei Monitor Failure → Trigger Recovery → Resume Main

### Tree Pattern Updates

- [ ] **Init_and_ExecuteSkill.bt.xml anpassen:**
  - Ersetze RetryUntilSuccess um Lock-Checks
  - Füge ContinuousHealthCheck in Parallel Branch ein
  - Füge RecoverySequence bei Health Check Failures ein

- [ ] **Neuer Tree: RecoveryTest.bt.xml**
  - Testet Recovery-Logic isoliert
  - Simuliert Lock-Verlust
  - Simuliert Startup Halted

---

## 🚀 Priorität 2: MQTT Messaging Integration (Phase 3)

### ✅ Skill Execution Messaging (FERTIG - 2/2) ✨
- [x] **ReadMqttSkillRequest** ✅
- [x] **SendSkillResponse** ✅

### 3.1 Remaining Messaging Nodes

- [ ] **UpdateInventoryFromAction** - Aktualisiert Inventar nach Action-Completion
  - **Quelle:** Action.Effects oder Action.FinalResultData
  - **Liest:** ProductID, ProductType, CarrierID, SlotID
  - **Updated:** Context Storage-State
  - **Sendet:** InventoryMessage via MQTT (optional)

- [ ] **UpdateNeighborsFromAction** - Aktualisiert gekoppelte Module nach Action
  - **Quelle:** Action.Effects (gekoppelte/entkoppelte Module)
  - **Updated:** Context Neighbors-State
  - **Sendet:** NeighborMessage via MQTT (optional)

### 3.2 Generic Messaging Nodes (Inter-Agent Communication)

- [ ] **SendMessage** - Sendet generische I4.0 Message
  - **Parameter:** 
    - AgentId (string) - Empfänger
    - MessageType (string) - "inform", "request", "consent", "refuse"
    - InteractionElements (List<ISubmodelElement>)
    - Topic (string, optional) - Falls nicht Default-Topic
  - **Nutzt:** I40MessageBuilder
  - **Returns:** Success wenn gesendet

- [ ] **WaitForMessage** - Wartet auf eingehende Message
  - **Parameter:**
    - ExpectedType (string, optional) - Filter nach MessageType
    - ExpectedSender (string, optional) - Filter nach Sender
    - TimeoutSeconds (int, default=30)
  - **Returns:** Success mit Message oder Failure bei Timeout
  - **Speichert:** `LastReceivedMessage` im Context

- [ ] **SendStateMessage** - Sendet Modulzustände via MQTT
  - **Topic:** `/Modules/{ModuleID}/State/`
  - **Struktur:** SubmodelElementCollection mit:
    - ModuleLocked (bool)
    - StartupSkill running (bool)
    - ModuleReady (bool)
    - ModuleState (LifecycleStateEnum)
  - **Frame Type:** "inform"
  - **Returns:** Success

- [ ] **ReadInventoryMessage** - Liest Inventar von Remote-Modul
  - **Topic:** `/Modules/{ModuleID}/Inventory/`
  - **Struktur:** JSON Array mit Storage Slots:
    - Storage/RFIDStorage (name)
    - slots[index].content { CarrierID, CarrierType, ProductType, ProductID, IsSlotEmpty }
  - **Returns:** Success mit Inventory

- [ ] **ReadNeighborMessage** - Liest gekoppelte Module
  - **Topic:** `/Modules/{ModuleID}/Neighbors/`
  - **Struktur:** SubmodelElementList mit Module-IDs
  - **Returns:** Success mit Neighbors List

### 3.3 Integration mit I4.0-Sharp-Messaging

- [ ] **MessageFrame Builder verwenden**
  - Alle Messaging Nodes nutzen `I40MessageBuilder`
  - Frame erstellen mit: Sender, Receiver, Type, ConversationId
  - InteractionElements hinzufügen (Action, Properties, Collections)

- [ ] **MessagingClient aus Context holen**
  - Nach `ConnectToMessagingBrokerNode` ist Client verfügbar
  - `var client = Context.Get<MessagingClient>("MessagingClient");`

- [ ] **Topic Subscribe/Unsubscribe Logic**
  - ReadMqttSkillRequest: Subscribe zu SkillRequest Topic
  - Auto-Unsubscribe bei Node Abort/Reset

---

## Priorität 3: Constraint & Precondition Logic - ⏳ PHASE 4

### 4. Constraint Nodes

- [ ] **RequiresMaterial** - Prüft Material-Verfügbarkeit
  - Parameter: itemId, quantity, moduleId
  - Nutzt ReadInventoryMessage oder CheckInventory
  - Returns: Success wenn genug Material

- [ ] **RequiresTool** - Tool-Constraints
  - Prüft Tool-Verfügbarkeit im Inventar

- [ ] **ModuleReady** - Aggregierte Readiness-Prüfung
  - Kombiniert: CheckReadyState, CheckErrorState, CheckLockedState(false)
  - Returns: Success nur wenn alle Checks erfolgreich

- [ ] **ProductMatchesOrder** - Prüft ob richtiges Produkt geladen
  - Vergleicht Action.InputParameters.ProductType mit Storage Content

- [ ] **ProcessParametersValid** - Validiert Prozessparameter
  - Prüft InputParameters gegen Preconditions/Constraints

- [ ] **SafetyOkay** - Sicherheits-Constraints
- [ ] **RequireNeighborAvailable** - Nachbar verfügbar? (nutzt ReadNeighborMessage)

### 5. Precondition Execution Logic

- [ ] **EvaluatePreconditions** - Führt alle Preconditions aus Action aus
  - Parameter: Action.Preconditions (SubmodelElementCollection)
  - Führt alle Constraint Nodes sequenziell aus
  - Returns: Success nur wenn alle erfüllt

---

## Priorität 4: Schedule & Planning - ⏳ PHASE 5

### 6. Planning Nodes (für Planning Agent - später)

- [ ] **ExecuteCapabilityMatchmaking** - Analysiert Capability-Match
- [ ] **SchedulingExecute** - Scheduling Algorithmus
- [ ] **CalculateOffer** - Berechnet Angebot
- [ ] **SendOffer** - Sendet Angebot
- [ ] **UpdateMachineSchedule** - Aktualisiert Schedule
- [ ] **RequestTransport** - Fragt Transporte an

---

## Priorität 5: Advanced Monitoring - ⏳ PHASE 6

### 7. Extended Monitoring Nodes

- [ ] **CheckAlarmHistory** - OPC UA Alarm Log Query
- [ ] **CheckScheduleFreshness** - Schedule Drift Detection
- [ ] **CheckTimeDrift** - NTP Time Synchronization
- [ ] **CheckNeighborAvailability** - Nachbar-Modul prüfen
- [ ] **CheckTransportArrival** - Transport-Ankunft
- [ ] **CheckCurrentSchedule** - Schedule Konsistenz
- [ ] **CheckEarliestStartTime** - Zeitfenster-Constraints
- [ ] **CheckDeadlineFeasible** - Deadline-Machbarkeit
- [ ] **CheckModuleCapacity** - Kapazitäts-Prüfung

### 8. Event Nodes (Reactive)

- [ ] **OnSkillStateChanged** - Event-Trigger bei Skill State Change
- [ ] **OnInventoryChanged** - Event bei Inventory-Änderung
- [ ] **OnNeighborChanged** - Event bei Neighbor-Änderung

---

## 📊 Implementierungs-Reihenfolge (AKTUALISIERT)

### ✅ Phase 0: Infrastructure (ABGESCHLOSSEN)
1. ✅ MqttLogger
2. ✅ Trees bereinigt

### ✅ Phase 1: Core Monitoring (ABGESCHLOSSEN)
1. ✅ CheckReadyState, CheckErrorState, CheckLockedState, MonitoringSkill

### ✅ Phase 2: Skill Control (ABGESCHLOSSEN)
1. ✅ WaitForSkillState, AbortSkill, PauseSkill, ResumeSkill, RetrySkill
2. ✅ MONITORING_AND_SKILL_NODES.md Dokumentation

### 🔥 Phase 3.5: Recovery & Monitoring (JETZT - KRITISCH!)
1. [ ] **HaltAllSkillsNode** - Stop alle Skills bei Recovery
2. [ ] **EnsureStartupRunningNode** - Garantiert Startup läuft
3. [ ] **EnsureModuleLockedNode** - Garantiert Lock aktiv
4. [ ] **RecoverySequenceNode** - Orchestriert Recovery
5. [ ] **ContinuousHealthCheckNode** - Parallel Monitor
6. [ ] **MonitorAndRecoverNode** - Wrapper mit Recovery
7. [ ] **Init_and_ExecuteSkill.bt.xml anpassen** - Neue Pattern einbauen
8. [ ] **RecoveryTest.bt.xml erstellen** - Isolierter Recovery Test
9. [ ] **Runtime Test:** Operator überschreibt Lock → Auto-Recovery
10. [ ] **Dokumentation:** RECOVERY_AND_MONITORING.md

**Status:** 🔥 **0/10 Recovery Tasks - HÖCHSTE PRIORITÄT**

### 🔄 Phase 3: Messaging Integration (DANACH)
1. [x] **ReadMqttSkillRequest** - Action von Planning Agent lesen ✅
2. [x] **SendSkillResponse** - ActionState zurücksenden ✅
   - Sendet komplette Action mit Status, InputParameters, FinalResultData
3. [ ] UpdateInventoryFromAction - Inventar nach Action aktualisieren
4. [ ] UpdateNeighborsFromAction - Gekoppelte Module aktualisieren
5. [ ] SendMessage - Generische I4.0 Message senden
6. [ ] WaitForMessage - Auf eingehende Message warten
7. [ ] SendStateMessage - Modulzustände publizieren
8. [ ] ReadInventoryMessage - Remote Inventar lesen
9. [ ] ReadNeighborMessage - Gekoppelte Module lesen
10. [ ] **Tests:** MQTT Integration Tests
11. [ ] **Dokumentation:** MESSAGING_NODES.md erstellen

**Status:** 🎉 **2/9 Core Messaging Nodes implementiert!**
- ✅ ReadMqttSkillRequest - Empfängt Actions via MQTT
- ✅ SendSkillResponse - Sendet ActionState Updates mit kompletter Action
- ✅ Runtime Placeholder Replacement ({MachineName} → "ScrewingStation")
- ✅ CheckReadyState Logic korrigiert (gelockt = ready)

### ⏳ Phase 4: Constraints & Preconditions
1. [ ] RequiresMaterial, ModuleReady, ProductMatchesOrder
2. [ ] EvaluatePreconditions - Action.Preconditions ausführen
3. [ ] **Tests:** Constraint Logic Tests
4. [ ] **Dokumentation:** CONSTRAINT_NODES.md

### ⏳ Phase 5: Planning (Planning Agent)
1. [ ] CapabilityMatchmaking, Scheduling, Bidding Nodes

### ⏳ Phase 6: Advanced Monitoring
1. [ ] Extended Monitoring Nodes (Alarm, Drift, Schedule)
2. [ ] Event Nodes (OnSkillStateChanged, etc.)

---

## 🎯 Erfolgs-Kriterien

### ✅ Phase 1+2 Erfolgreich:
- [x] Alle 9 Monitoring + Skill Control Nodes kompilieren und laufen
- [x] MONITORING_AND_SKILL_NODES.md dokumentiert

### 🔥 Phase 3.5 Erfolgreich wenn:
- [ ] **Lock-Verlust Recovery:** Operator überschreibt Lock → Tree detected → Auto Re-Lock → Startup Restart → Resume
- [ ] **Startup Halted Recovery:** Operator haltet Startup → Tree detected → Auto Restart → Resume
- [ ] **Timeout Logic:** Tree nicht endlos (max 90 Sekunden für Recovery)
- [ ] **Parallel Monitoring:** Continuous Health Check läuft parallel zur Execution
- [ ] **Recovery Test:** RecoveryTest.bt.xml läuft erfolgreich durch
- [ ] **No Infinite Loops:** Tree terminiert immer (Success/Failure) nach max 120 Sekunden

### 🔄 Phase 3 Erfolgreich wenn:
- [ ] Execution Agent kann Action von Planning Agent empfangen
- [ ] Execution Agent kann ActionState Updates senden
- [ ] State Messages werden korrekt publiziert
- [ ] Inventar wird nach Action-Completion aktualisiert
- [ ] Integration Test: Planning Agent → Execution Agent → Skill Execution

### ⏳ Minimal Viable Execution Agent kann (nach Phase 4):
1. ✅ OPC UA Verbindung aufbauen
2. ✅ Modul-Readiness prüfen
3. [ ] **Action von MQTT lesen** (Planning Agent → Execution Agent)
4. [ ] **Preconditions validieren** (Material, Tools aus Action.Preconditions)
5. ✅ Skill ausführen mit Parametern
6. ✅ Auf Skill Completion warten
7. [ ] **ActionState zurücksenden** (Execution Agent → Planning Agent)
8. [ ] **Inventar aktualisieren** (aus Action.FinalResultData)
9. ✅ Fehler loggen

---

## 📁 Dateistruktur (AKTUALISIERT)

```
MAS-BT/
├── BehaviorTree/
│   └── Nodes/
│       ├── MonitoringNodes.cs              [✅ Phase 1 - 4 Nodes]
│       ├── SkillControlNodes.cs            [✅ Phase 2 - 5 Nodes]
│       ├── RecoveryNodes.cs                [🔥 Phase 3.5 - NEU - 6 Nodes]
│       └── MessagingNodes.cs               [🔄 Phase 3 - 2/9 Complete]
├── Services/
│   └── MqttLogger.cs                       [✅ Phase 0]
├── Trees/
│   ├── Init_and_ExecuteSkill.bt.xml        [🔥 BUGGY - Needs Recovery Pattern]
│   └── Examples/
│       ├── SkillLifecycleTest.bt.xml       [✅ Phase 2 Test]
│       ├── RecoveryTest.bt.xml             [🔥 Phase 3.5 Test - NEU]
│       └── ActionExecutionTest.bt.xml      [🔄 Phase 3 Test]
└── docs/
    ├── MONITORING_AND_SKILL_NODES.md       [✅ Phase 1+2 Doku]
    ├── RECOVERY_AND_MONITORING.md          [🔥 Phase 3.5 Doku - NEU]
    ├── MESSAGING_NODES.md                  [🔄 Phase 3 Doku]
    └── CONSTRAINT_NODES.md                 [⏳ Phase 4 Doku]
```

---

## 🚀 Nächste Schritte (KLAR DEFINIERT)

1. ✅ Phase 1+2 abgeschlossen
2. ✅ Phase 3 teilweise (2/9 Messaging Nodes)
3. 🔥 **JETZT: Phase 3.5 - KRITISCHE BUGS FIXEN**
   - **HaltAllSkillsNode** implementieren
   - **EnsureStartupRunningNode** implementieren
   - **EnsureModuleLockedNode** implementieren
   - **RecoverySequenceNode** implementieren
   - **Init_and_ExecuteSkill.bt.xml** mit Recovery Pattern updaten
   - **RecoveryTest.bt.xml** erstellen
   - **Runtime Test:** Lock-Verlust Recovery
4. [ ] Phase 3 fortsetzen - Remaining Messaging Nodes
5. [ ] Phase 4 - Constraints & Preconditions

---

## 📊 Projekt-Statistik

- **Nodes implementiert:** 21 (9 Core + 5 Skill Control + 5 Config + 2 Messaging)
- **Phase 1+2:** ✅ 100% Complete
- **Phase 3:** 🔄 2/9 Nodes Complete
- **Phase 3.5:** 🔥 0/6 Recovery Nodes (KRITISCH)
- **Trees bereinigt:** 3 (~50% Code-Reduktion)
- **Compile-Status:** ✅ 0 Errors
- **Runtime Status:** 🐛 4 Kritische Bugs identifiziert
- **Noch zu implementieren:** ~39 Nodes aus specs.json

---

## 💡 Wichtige Architektur-Erkenntnisse

### Recovery Pattern für robuste Execution ⭐
```xml
<Parallel name="ExecuteWithRecovery" policy="ParallelAll">
  <!-- Main Execution -->
  <Sequence name="MainExecution">
    <ExecuteSkill SkillName="Screw"/>
  </Sequence>
  
  <!-- Continuous Health Monitor -->
  <RepeatUntilFailure name="HealthMonitor">
    <Sequence name="CheckHealth">
      <Fallback name="HealthCheckWithRecovery">
        <!-- Try Health Check -->
        <Sequence name="HealthChecks">
          <CheckLockStatus ModuleName="ScrewingStation"/>
          <CheckStartupSkillStatus ModuleName="ScrewingStation"/>
        </Sequence>
        
        <!-- If Failed → Trigger Recovery -->
        <RecoverySequence ModuleName="ScrewingStation"/>
      </Fallback>
      
      <Wait DelayMs="1000"/>
    </Sequence>
  </RepeatUntilFailure>
</Parallel>
```

### Idempotent Recovery Nodes ⭐
- **EnsureStartupRunning:** Check State first → only restart if needed
- **EnsureModuleLocked:** Check Lock first → only re-lock if needed
- Macht Recovery Nodes wiederholbar ohne Side-Effects

### SkillRequest/SkillResponse sind Actions! ⭐
```csharp
// Planning Agent sendet:
var action = new Action("Action001", "RetrieveToPortLogistic", ...);
var message = new I40MessageBuilder()
    .From("Module2_Planning_Agent")
    .To("Module2_Execution_Agent")
    .WithType("request")
    .AddElement(action)
    .Build();

// Execution Agent empfängt und führt aus:
var action = message.InteractionElements[0] as Action;
var skillName = action.GetProperty("ActionTitle").Value; // "RetrieveToPortLogistic"
var parameters = action.GetCollection("InputParameters");

// Execution Agent antwortet:
var responseAction = action.Clone();
responseAction.AddProperty("ActionState", "Running");
var response = new I40MessageBuilder()
    .From("Module2_Execution_Agent")
    .To("Module2_Planning_Agent")
    .WithType("update")
    .AddElement(responseAction)
    .Build();
```

### Lifecycle States für Module ⭐
- **Unconfigured** → **Configuring** → **Inactive**
- **Inactive** → **Activating** → **Active**
- **Active** → **Deactivating** → **Inactive**
- **Inactive** → **ShuttingDown** → **Finalized**
- **Any** → **ErrorProcessing** → **Inactive**

### Message Frame Types ⭐
- **request** - Planning Agent fragt Action an
- **consent** - Execution Agent akzeptiert
- **refuse** - Execution Agent lehnt ab
- **update** - Execution Agent sendet Progress
- **inform** - Broadcast (State, Log)


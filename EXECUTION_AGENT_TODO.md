# ToDo-Liste: Execution Agent Basis-Implementierung
## Update: 
### Letzte Änderungen (Stand 2025-12-07)

- `ReadMqttSkillRequestNode` parst eingehende MQTT SkillRequest-Nachrichten jetzt vollständig in ein AAS `Action`-Objekt
  - Speichert `CurrentAction`, `ActionId`, `ConversationId`, `OriginalMessageId`, `RequestSender` und eine case-insensitive `InputParameters`-Map im BT-Context.
  - Vorteil: Downstream-Nodes (z.B. `ExecuteSkillNode`, `SendSkillResponseNode`) arbeiten direkt mit dem AAS-`Action`-Objekt.

- Lock-Strategie geändert: Retry/Waiting für Lock-Akquise wurde aus `RemoteModule` entfernt und in das BT-Node `LockResourceNode` verlagert.
  - `RemoteModule.LockAsync` führt nur noch eine direkte Init/Acquire-Operation aus; Wiederholungen / Timeout-Logik liegt nun in der Tree-Node.
  - Dadurch wird das Verhalten transparent und steuerbar durch die Tree-Logik (keine verdeckten Warte-Loops mehr im Client).

- `EnsurePortsCoupledNode`
  - Setzt jetzt sowohl die Kontext-Flags `portsCoupled` als auch `coupled` (mittels `UpdateCouplingFlags`) um Inkonsistenzen zu vermeiden.
  - Versucht, vorhandene `CoupleSkill`-Instanzen zu starten (bzw. bei Bedarf Reset+Start), und meldet Erfolg/Misserfolg über Logs und Kontext.
  - Node ist im `NodeRegistry` registriert und wird in den Beispiel-Bäumen vor dem `StartupSkill` verwendet (z.B. `RetryUntilSuccess` um Kopplung sicherzustellen).

- Diagnostics/Tools:
  - `RemoteInspector` erweitert: listet Ports mit `Coupled`-Status, `CoupleSkill`-Verfügbarkeit, `Active` und `PartnerTag`.

### Status / Hinweise

- Action-Serialisierung: Helper in `I40MessageBuilder` (`CreateStringProperty`, `CreateBooleanProperty` etc.) wurden ergänzt um `valueType` korrekt zu setzen; Build/Compile erfolgreich, Serialisierung von Property-Werten wurde noch in Integrationstests verifiziert.
- Open: Paar Laufzeitfälle zeigten `BadInvalidState` bei `Start` eines CoupleSkill — `RemotePort.CoupleAsync` versucht `Reset`+`Start` als Recovery; falls weiterhin Fehler auftreten, empfiehlt sich zusätzliches Logging und ggf. längere Timeouts.

**Aktueller Stand (Stand 2025-12-07 16:20)**

- Reconnect / Session-Health
  - `UaClient` hat einen `KeepAlive`-Handler erhalten, der bei non-Good-Status `DisconnectAsync()` aufruft, damit die Reconnect-Logik des Servers zeitnah greift.
  - `RemoteServer.AutoReconnectLoop` wurde erweitert: detailliertes Logging, nummerierte Reconnect-Versuche, Backoff, und bei erfolgreichem Reconnect werden Discovery, Component-Discovery und Subscription-Setup erneut ausgeführt.

- Reinitialisierung nach Reconnect
  - Nach erfolgreichem Reconnect führt `RemoteServer` erneut aus: `IterateMachinesAsync`, `DiscoverComponentsAsync`, `SetupAllSubscriptionsAsync`.
  - Anschließend wird für alle Module `EnableAutoRecoveryAsync()` aufgerufen und `EnsureRecoveryAsync("Reconnect")` gestartet, damit Module wieder gelockt, gekoppelt und der `StartupSkill` sichergestellt werden.

- RemoteModule / Recovery
  - `RemoteModule.TriggerRecoveryAsync` wurde nicht-destruktiv umgebaut: es **stoppt keine bereits laufenden Skills** mehr. Stattdessen:
    - Schritt 1: Re-lock (erforderlich bevor Skills manipuliert werden)
    - Schritt 2: Für alle Ports `CoupleAsync` aufrufen, falls noch nicht gekoppelt
    - Schritt 3: `StartupSkill` nur dann starten, wenn er nicht bereits `Running` ist
  - Neue öffentliche Methode `RemoteModule.EnsureRecoveryAsync(string reason)` wurde hinzugefügt (Wrapper für `TriggerRecoveryAsync`) und wird vom `RemoteServer` nach Reconnect verwendet.

- Behavior Tree / Nodes
  - `EnsurePortsCoupledNode` ist implementiert und in Bäumen vor `StartupSkill` verwendbar; setzt Kontextflags `portsCoupled` und `coupled`.
  - `ReadMqttSkillRequestNode` parst SkillRequest-Messages vollständig und schreibt `CurrentAction` + case-insensitive `InputParameters` in den Tree-Context.
  - Lock-Policy: Retry/Wait für Locks ist in Tree-Node `LockResourceNode` verlagert (kein verstecktes Warten mehr im Client).

- Messaging / Notifier
  - `RemoteServerMqttNotifier` wurde implementiert und in `ConnectToModuleNode` registriert; publishen von AAS-`LogMessage` bei ConnectionLost/Established.

- Storage MQTT OnChange
  - `EnableStorageChangeMqtt`-Node registriert und in den Bäumen (`Init_and_ExecuteSkill`, Tests) verdrahtet; `RemoteModule` subscribed Storage/Slot-Variablen und `StorageMqttNotifier` publisht Änderungen sofort via MQTT.

- Recovery Nodes
  - `HaltAllSkillsNode`, `EnsureStartupRunningNode`, `EnsureModuleLockedNode`, `RecoverySequenceNode` sind implementiert und im `NodeRegistry` registriert (noch nicht flächig in die Bäume integriert).

- Messaging Nodes
  - `SendStateMessage`, `WaitForMessage`, `UpdateInventoryFromAction`, `EnableStorageChangeMqtt` vorhanden; `SendMessage` existiert, nutzt aber noch einen Mock statt I4.0-Sharp-Messaging.

### Neue Anforderungen (Queue + Preconditions) – priorisiert
- SkillRequest bewirkt nur das Einreihen einer Action in die Execution-Queue (keine Sofort-Execution). `consent/refuse` signalisiert Annahme/Ablehnung der Queue-Aufnahme; Consent kann optional die geplante Startposition/Schätzwartedauer kommunizieren.
- Queue-Handling:
  - Dequeue-Strategie: Priorität/Deadline/FCFS; nicht startbare Jobs (Preconditions fail) werden übersprungen, nächste startfähige Action läuft. Nach jedem Durchlauf kann erneut versucht werden, blockierte Jobs zu starten (mit Backoff).
  - Cancel/Remove: Planning-Agent kann per SkillRequest gezielt Queue-Elemente löschen (per ActionId/ConversationId); best effort, Rückmeldung via ActionUpdate.
  - Backpressure: Falls Queue voll/überlastet → `refuse` mit Grund „queue-full“.
- Preconditions-Integration (AAS-Datenmodell): Jede Precondition ist ein SMC mit `PreconditionType` (Enum) und `ConditionValue` (SMC). Für jetzt nur `InStorage`:
  - `PreconditionType` ∈ `PreconditionsEnum` (initial nur `InStorage`).
  - `ConditionValue` enthält zwei Properties: `SlotContentType` (Enum `SlotContentTypeEnum` mit Werten `CarrierId`, `CarrierType`, `ProductType`, `EmptySlot`) und `SlotValue` (string).
- Status/Rückmeldungen und Messaging:
  - Jeder Preconditions-Retry erzeugt eine ActionUpdate mit Hinweis „preconditions not satisfied“ (inkl. fehlendem `SlotContentType`/`SlotValue`).
  - Wenn Mapping fehlt, `ActionStatusEnum` um `PRECONDITION_FAILED` erweitern; anderenfalls bestehendes Message-Frame-Field nutzen (Type `update`).
  - Erfolgreicher Start → ActionUpdate `executing`; Completion → `done`; Abbruch → `aborted`; Fehler → `error`.
  - Optional: Queue-Telemetrie (Queue-Länge, ältester Wartezeitpunkt) als Log/StateMessage.

- Build & Lauf
  - Build erfolgreich. Lokaler Lauf zeigte: KeepAlive → Disconnect → Reconnect → Re-browse → Module-Recovery (Re-lock, Couple, Startup) — entsprechende Logs vorhanden.

**Offene Probleme / Beobachtungen**

- Transiente `BadInvalidState`-Fehler bei `Start`/`Reset` von CoupleSkill und gelegentlich beim `StartupSkill` (Skill-Zustände wie `11` oder `17` werden beobachtet). Ursache: Timing / State-Machine des Remote-Servers; Recovery versucht `Reset`+`Start` als Workaround.
- In einigen Fällen konnte `StartupSkill` nicht gestartet werden, weil er nicht im erwarteten `Ready`-Zustand war (z. B. aktuell numerischer Status `17`).

**Empfohlene nächste Schritte**

- Kurzfristig (schnelle Wins):
  - Skill-State-Logging: Logge numerischen Skill-State zusammen mit einer menschenlesbaren Mapping-Tabelle (z. B. `11 -> Ready`, `17 -> <meaning>`), damit Ursachen leichter analysierbar.
  - Erhöhe Timeout/Retry für `RemotePort.CoupleAsync` und `RemoteSkill` Reset/Start-Pfade (z. B. 3 Retries, 2s Backoff) für stabilere Recovery.

- Mittelfristig:
  - Schreibe Integrationstest, der den OPC UA-Server kurz stoppt/starts und die vollständige Reconnect+Recovery-Pipeline prüft (Assertions auf MQTT-Log-Nachrichten und Skill-States).
  - Betrachte parallele Triggerung von `EnsureRecoveryAsync` für mehrere Module (mit begrenzter Parallelität), um die Reinitialisierung bei vielen Modulen zu beschleunigen.

Diese Sektion dokumentiert den aktuellen Implementations- und Laufzeitstand. Weiter unten bleibt die ToDo-Liste für offene Nodes und Prioritäten bestehen.

## 🚨 KRITISCHE BUGS - SOFORT FIXEN (Phase 3.5)

### 🐛 Bug #1: Lock-Verlust wird nicht recovered
**Problem:** 
- Operator überschreibt Lock manuell im OPC UA
- CheckLockStatus erkennt Lock-Verlust (returns Failure)
- Tree läuft aber weiter ohne Re-Lock (800+ Ticks, 87+ Sekunden)
- Keine automatische Recovery

**Lösung:**
- [ ] **ContinuousHealthCheck/MonitorAndRecover** – Dauer-Monitor für Lock/Startup/Error, löst bei Failure die `RecoverySequence` aus.
- [ ] **RecoverySequence nutzen** – Monitor-Branch in `Init_and_ExecuteSkill.bt.xml` (und anderen Bäumen) soll `RecoverySequence` statt ad-hoc Unlock/Relock ausführen.
- [ ] **Runtime-Test:** Operator überschreibt Lock → RecoverySequence (HaltAllSkills → EnsureModuleLocked → EnsureStartupRunning) läuft durch.

### 🐛 Bug #2: StartupSkill Halted wird nicht restarted
**Problem:**
- StartupSkill geht auf Halted (z.B. durch Operator Reset)
- CheckStartupSkillStatus erkennt Halted-State
- Kein automatischer Restart

**Lösung:**
- [ ] **Monitor an RecoverySequence koppeln** – Wenn StartupSkill Halted erkannt wird, RecoverySequence auslösen (nutzt bereits vorhandenes `EnsureStartupRunning`).
- [ ] **Recovery-Testbaum erweitern** – Szenarien Lock-Verlust + Startup halted abdecken (ErrorRecoveryTest erweitern oder dedizierten Recovery-Test ergänzen).

## 🚀 Priorität 1: Recovery & Monitoring Logic (JETZT - Phase 3.5)

- [ ] **ContinuousHealthCheck/MonitorAndRecover Node** bauen: Dauer-Monitor (Lock/Startup/Error) der `RecoverySequence` triggert.
- [ ] **Bäume umstellen:** Monitor-Branch in `Init_and_ExecuteSkill.bt.xml` (und ggf. `ModuleInitializationTest`, `ActionExecutionTest`) auf `RecoverySequence` + `HaltAllSkills`/`EnsureModuleLocked`/`EnsureStartupRunning` umstellen; kein manuelles Unlock/Relock.
- [ ] **Recovery-Testbaum**: Lock-Verlust + Startup Halted abdecken (bestehenden `ErrorRecoveryTest` erweitern oder neuen `RecoveryTest` erstellen) inkl. Assertions auf Logs/States.
- [ ] **Runtime-Test**: Operator überschreibt Lock → RecoverySequence greift; Logs und Zustandswechsel verifizieren.
- [ ] **Dokumentation**: `RECOVERY_AND_MONITORING.md` mit finalem Pattern (ParallelAll + RecoverySequence) ergänzen.

## 🚀 Priorität 0: Queue & Preconditions (NEU – vor Recovery)

- [ ] **Execution-Queue Flow**: SkillRequest → enqueue; `consent/refuse` quittiert Queue-Aufnahme (Refuse bei queue-full/ungültig). Dequeue nach Priorität/Deadline/FCFS; wenn Preconditions fail → überspringen, nächste Action starten, späterer Retry mit Backoff.
- [ ] **Queue-API**: SkillRequest für Cancel/Remove (by ActionId/ConversationId); Rückmeldung via ActionUpdate (cancelled/removed oder not-found).
- [ ] **Action-Status/Messaging**: ActionUpdate bei jedem Preconditions-Retry mit Grund „preconditions not satisfied“ (inkl. SlotContentType/SlotValue); falls nötig `PRECONDITION_FAILED` in ActionStatusEnum + Mapping/Frame-Type etablieren.
- [ ] **Preconditions-Datenmodell**: `PreconditionsEnum` (initial `InStorage`), `ConditionValue` SMC mit `SlotContentType` (`SlotContentTypeEnum`: CarrierId, CarrierType, ProductType, EmptySlot) + `SlotValue` (string).
- [ ] **Preconditions-Check im Dispatcher**: Vor Start Preconditions evaluieren; bei Fail → ActionUpdate + Skip/Retry (Backoff konfigurierbar), kein Hard-Failure der Queue.
- [ ] **Doku & Telemetrie**: Queue-Flow (enqueue, consent/refuse, dequeue, skip/ retry, cancel), Preconditions-Schema, optionale Queue-Metriken (Länge, oldest-waiting) beschreiben.

---

## 🚀 Priorität 2: MQTT Messaging Integration (Phase 3)

- [ ] **SendMessageNode finalisieren** – I4.0-Sharp-Messaging nutzen statt Mock, Topics/ConversationId/InteractionElements korrekt setzen.
- [ ] **UpdateNeighborsFromAction** – Effekte aus Action auswerten und NeighborMessage publizieren.
- [ ] **ReadInventoryMessage** – Inventory-Topic lesen/parsen und in den Context legen.
- [ ] **ReadNeighborMessage** – Neighbor-Topic lesen/parsen und in den Context legen.
- [ ] **MQTT-Integrationstests** – SendMessage/WaitForMessage/SendStateMessage/UpdateInventoryFromAction + neue Nodes automatisiert testen.
- [ ] **Dokumentation** – `MESSAGING_NODES.md` mit finalem API/Topic-Wiring aktualisieren.

---

## Priorität 3: Constraint & Precondition Logic - ⏳ PHASE 4

- [ ] **EvaluatePreconditions** – Aggregator, führt vorhandene Constraint-Nodes (RequiresMaterial, RequiresTool, ModuleReady, ProductMatchesOrder, ProcessParametersValid, SafetyOkay, RequireNeighborAvailable) anhand von Action.Preconditions aus.
- [ ] **Constraint-Tests** – Integrationstests für die Constraint-Nodes (inkl. Mock-Inventory/Neighbor-Daten).
- [ ] **Dokumentation** – `CONSTRAINT_NODES.md` um EvaluatePreconditions-Usage und Beispiele ergänzen.

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

### 🔥 Phase 3.5: Recovery & Monitoring (JETZT)
1. [ ] ContinuousHealthCheck/MonitorAndRecover Node
2. [ ] RecoverySequence in `Init_and_ExecuteSkill.bt.xml` (+ Tests) verdrahten
3. [ ] Recovery-Testbaum (Lock-Verlust + Startup Halted)
4. [ ] Runtime-Test: Lock-Override
5. [ ] Dokumentation: `RECOVERY_AND_MONITORING.md`

**Status:** Recovery-Nodes sind implementiert; Monitoring/Wiring/Tests/Doku stehen aus.

### 🔄 Phase 3: Messaging Integration (danach)
1. [ ] SendMessageNode auf I4.0-Sharp-Messaging umbauen
2. [ ] UpdateNeighborsFromAction
3. [ ] ReadInventoryMessage
4. [ ] ReadNeighborMessage
5. [ ] MQTT-Integrationstests + Doku

**Status:** Basis-Nodes (`ConnectToMessagingBroker`, `ReadMqttSkillRequest`, `SendSkillResponse`, `SendStateMessage`, `WaitForMessage`, `UpdateInventoryFromAction`, `EnableStorageChangeMqtt`) vorhanden; Nachbarn/Inventory-Pull + echtes SendMessage fehlen.

### ⏳ Phase 4: Preconditions
1. [ ] EvaluatePreconditions
2. [ ] Constraint-Integrationstests + Doku

### 🚀 Phase 0: Queue & Preconditions (NEU – höchste Prio)
1. [ ] Execution-Queue Flow (SkillRequest → enqueue, consent/refuse, dequeue, skip-on-precondition-fail, cancel per SkillRequest)
2. [ ] ActionStatus-Erweiterung/Mapping für Preconditions-Fail (z.B. PRECONDITION_FAILED)
3. [ ] Preconditions-Datenmodell (PreconditionsEnum: InStorage; ConditionValue: SlotContentType + SlotValue)
4. [ ] Preconditions-Check im Dispatcher (Retry/Backoff, ActionUpdate bei Fail)
5. [ ] Doku: Queue- und Preconditions-Flows

### ⏳ Phase 5: Planning (Planning Agent)
- [ ] CapabilityMatchmaking, Scheduling, Bidding Nodes

### ⏳ Phase 6: Advanced Monitoring (Backlog)
- [ ] Extended Monitoring Nodes (Alarm, Drift, Schedule, Neighbor Availability, Event-Triggers)

---

## 🎯 Erfolgs-Kriterien


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
3. [ ] **Action von MQTT lesen** (Planning Agent → Execution Agent)
4. [ ] **Preconditions validieren** (Material, Tools aus Action.Preconditions)
7. [ ] **ActionState zurücksenden** (Execution Agent → Planning Agent)
8. [ ] **Inventar aktualisieren** (aus Action.FinalResultData)

---

## 📁 Dateistruktur (AKTUALISIERT)

```
MAS-BT/
├── Nodes/
│   ├── Configuration/ (ConnectToModule, EnsurePortsCoupled, ReadConfig, …)
│   ├── Locking/ (LockResource, UnlockResource, CheckLockStatus)
│   ├── Recovery/ (`HaltAllSkills`, `EnsureModuleLocked`, `EnsureStartupRunning`, `RecoverySequence`)
│   ├── Messaging/ (`ConnectToMessagingBroker`, `ReadMqttSkillRequest`, `SendSkillResponse`, `SendStateMessage`, `WaitForMessage`, `UpdateInventoryFromAction`, `EnableStorageChangeMqtt`, `SendMessage` (mock), …)
│   ├── Constraints/ (RequiresMaterial, RequiresTool, ModuleReady, ProcessParametersValid, …)
│   ├── SkillControl/ (ExecuteSkill, WaitForSkillState, RetrySkill, Pause/Resume/Abort/Reset)
│   ├── Monitoring/ (CheckReadyState, CheckErrorState, CheckStartupSkillStatus, ReadStorage)
│   └── Core/ (Wait, AlwaysSuccess, ForceFailure, SetBlackboardValue)
├── BehaviorTree/ (Engine + Serialization, `NodeRegistry.cs`)
├── Trees/
│   ├── Init_and_ExecuteSkill.bt.xml (mit EnableStorageChangeMqtt, einfachem Monitor)
│   └── Examples/
│       ├── ActionExecutionTest.bt.xml
│       ├── ErrorRecoveryTest.bt.xml
│       └── SkillLifecycleTest.bt.xml
├── Services/ (MqttLogger, StorageMqttNotifier)
├── docs/ (MONITORING_AND_SKILL_NODES.md, CONFIGURATION_NODES.md, RECOVERY_AND_MONITORING.md, MESSAGING_NODES.md, CONSTRAINT_NODES.md)
└── tests/
```

---

## 🚀 Nächste Schritte (KLAR DEFINIERT)

1. 🔥 ContinuousHealthCheck/MonitorAndRecover bauen und `Init_and_ExecuteSkill.bt.xml` auf `RecoverySequence` umstellen.
2. 🔥 Recovery-Testbaum (Lock-Verlust + Startup Halted) und manuellen Runtime-Test fahren.
3. 🔄 `SendMessageNode` auf echtes I4.0-Sharp-Messaging umbauen; MQTT-Integrationstests ergänzen.
4. 🔄 `UpdateNeighborsFromAction`, `ReadInventoryMessage`, `ReadNeighborMessage` implementieren.
5. ⏳ `EvaluatePreconditions` + Constraint-Integrationstests ergänzen.

---

## 📊 Projekt-Statistik

- **Nodes implementiert:** >40 (Core, Configuration, Locking, Monitoring, Recovery, Messaging, Constraints, SkillControl).
- **Recovery:** Nodes vorhanden; Continuous-Monitoring, Tree-Wiring und Tests fehlen.
- **Messaging:** Kernknoten vorhanden; `SendMessage` noch Mock, Neighbor/Inventory-Pull fehlt.
- **Constraints:** Einzel-Nodes vorhanden; `EvaluatePreconditions` + Tests offen.
- **Trees:** `Init_and_ExecuteSkill` mit einfachem Monitor + `EnableStorageChangeMqtt`; `ErrorRecoveryTest`/`ActionExecutionTest` vorhanden, Recovery-Monitoring noch ergänzen.
- **Build/Lauf:** Letzter `dotnet build` erfolgreich (Skill-Sharp-Client); `dotnet run -- Examples/ActionExecutionTest.bt.xml` in MAS-BT schlug fehl → nach Recovery-Wiring erneut prüfen.

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

### Execution-seitige Skill-Queue (Auftrags-Puffer) ⭐
- Queue gehört zum Execution Agent, weil er Lock/Startup/Recovery/Resource-Zustände kennt und Startzeiten realistisch entscheiden kann.
- Planning Agent liefert priorisierte Actions; Execution legt sie in eine Ready-Queue, validiert Preconditions (Lock, Startup, Material/Tool, Neighbor), startet wenn frei.
- Backpressure: Bei Busy/Queue-Full sendet Execution `refuse`/`busy` oder `update` mit Delay; Planning kann umplanen.
- Zustand halten: Queue-Einträge mit `ActionId`, Priorität, Deadline, Preconditions-Status, Retries, CurrentState (Pending, Running, Completed, Failed), ConversationId für Responses.
- Telemetrie: State-Updates/MQTT bei Enqueue/Start/Complete/Fail; optional Queue-Länge/Oldest-Waiting als Health-Metrik.
- Abbruch/Recovery: Bei RecoverySequence laufende Skills ggf. aborten/pausieren, Queue bleibt bestehen; nach Recovery werden Pending-Einträge erneut geprüft.


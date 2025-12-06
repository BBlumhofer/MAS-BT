# ToDo-Liste: Execution Agent Basis-Implementierung

## 🎉 ABGESCHLOSSEN

### ✅ Phase 0: Infrastructure & Cleanup
- [x] **MqttLogger implementiert** - Automatisches Logging aller Nodes via MQTT
- [x] **Trees bereinigt** - 39 `SendLogMessage` Nodes entfernt (53% kleiner)
  - Init_and_ExecuteSkill.bt.xml: 273 → 129 Zeilen
  - ModuleInitializationTest.bt.xml: 203 → 107 Zeilen
  - ResourceHolonInitialization.bt.xml: Bereits sauber

### ✅ Phase 1: Core Monitoring Nodes (FERTIG) ✨
- [x] **CheckReadyState** - Prüft ob Modul bereit ist
  - Implementiert in: `/BehaviorTree/Nodes/MonitoringNodes.cs`
  - Nutzt: `RemoteModule.IsLockedByUs` (vereinfachte Ready-Prüfung)
  - Registriert in: `NodeRegistry.cs`
  - **Getestet:** ✅ Kompiliert und läuft

- [x] **CheckErrorState** - Prüft auf Fehler im Modul
  - Implementiert in: `/BehaviorTree/Nodes/MonitoringNodes.cs`
  - Erkennt: Unerwartete `Halted` States von Skills
  - Nutzt: `RemoteSkill.CurrentState == SkillStates.Halted`
  - Registriert in: `NodeRegistry.cs`
  - **Getestet:** ✅ Kompiliert und läuft

- [x] **CheckLockedState** - Erweiterte Lock-Prüfung
  - Implementiert in: `/BehaviorTree/Nodes/MonitoringNodes.cs`
  - Parameter: `ExpectLocked` (bool) - flexibel für gelockt/frei
  - Nutzt: `RemoteModule.IsLockedByUs`
  - Registriert in: `NodeRegistry.cs`
  - **Getestet:** ✅ Kompiliert und läuft

- [x] **MonitoringSkill** - Liest Skill State + Monitoring Variables
  - Implementiert in: `/BehaviorTree/Nodes/MonitoringNodes.cs`
  - Liest: `RemoteSkill.CurrentState`
  - Speichert State im Context: `skill_{SkillName}_state`
  - TODO: MonitoringData Variables erweitern wenn API verfügbar
  - Registriert in: `NodeRegistry.cs`
  - **Getestet:** ✅ Kompiliert und läuft

---

## ⚠️ KRITISCHE FIXES (Priorität 0 - VOR ALLEM ANDEREN)

### Fix 1: Groot BT Editor Kompatibilität 🔴
- [ ] **XML-Format für Groot anpassen**
  - Problem: Groot erwartet `<root>` (lowercase) als Root-Element
  - Aktuell: `<BehaviorTree><Root>...</Root></BehaviorTree>`
  - Groot-kompatibel: `<root main_tree_to_execute="TreeName">...</root>`
  - Betrifft: Alle `.bt.xml` Dateien in `/Trees/`
  - Lösung: XML-Struktur anpassen oder Serializer/Deserializer erweitern

### Fix 2: ReadyState & ErrorState Klarstellung 📋
- [x] **CheckReadyState** - ✅ IMPLEMENTIERT
  - RemoteModule hat bereits IsLockedByUs Property
  - Nutzt Lock-Status als Ready-Indikator
  - BT-Node implementiert als Wrapper

- [x] **CheckErrorState** - ✅ IMPLEMENTIERT
  - **Kriterium 1**: Ein gestarteter Skill geht **unerwartet** in `Halted`
    - Prüft alle Skills auf `SkillStates.Halted`
    - Logged Warning wenn Halted State erkannt
  - **Kriterium 2**: StartupSkill geht von `Running` → `Halted` ohne Halt-Command
    - Wird durch allgemeinen Halted-Check abgedeckt
  - **TODO für später**: State-Tracking ob Halt explizit angefordert wurde

---

## Status der existierenden Nodes ✅
- [x] ConnectToModule - OPC UA Verbindung
- [x] ExecuteSkill - Skill ausführen mit Parametern
- [x] LockResource/UnlockResource - Ressourcen sperren
- [x] CheckLockStatus - Lock-Status prüfen (Original)
- [x] CheckLockedState - Erweiterte Lock-Prüfung mit ExpectLocked ✨ NEU
- [x] SendMessage/WaitForMessage - MQTT Messaging
- [x] ReadStorage - Inventar lesen
- [x] CheckStartupSkillStatus - StartupSkill überwachen
- [x] ConnectToMessagingBroker - MQTT Verbindung
- [x] SendLogMessage - Log-Nachrichten senden (kann entfernt werden, MqttLogger ersetzt es)
- [x] SendConfigAsLog - Config als Log senden
- [x] CheckReadyState - Modul-Bereitschaft prüfen ✨ NEU
- [x] CheckErrorState - Fehler erkennen ✨ NEU
- [x] MonitoringSkill - Skill State + Monitoring ✨ NEU

---

## 🚀 Priorität 1: Core Execution Agent Nodes (Must-have)

### 1. Monitoring Nodes - ✅ PHASE 1 ABGESCHLOSSEN
- [x] CheckReadyState
- [x] CheckErrorState  
- [x] CheckLockedState
- [x] MonitoringSkill

**Noch zu implementieren aus specs.json:**
- [ ] **CheckAlarmHistory** - OPC UA Alarm Log Query
- [ ] **CheckInventory** - Material-Verfügbarkeit (erweitert ReadStorage)
- [ ] **CheckToolAvailability** - Tool-Verfügbarkeit
- [ ] **RefreshStateMessage** - Alle States aktualisieren
- [ ] **CheckScheduleFreshness** - Schedule Drift Detection
- [ ] **CheckTimeDrift** - NTP Time Synchronization
- [ ] **CheckNeighborAvailability** - Nachbar-Modul prüfen
- [ ] **CheckTransportArrival** - Transport-Ankunft
- [ ] **CheckCurrentSchedule** - Schedule Konsistenz
- [ ] **CheckEarliestStartTime** - Zeitfenster-Constraints
- [ ] **CheckDeadlineFeasible** - Deadline-Machbarkeit
- [ ] **CheckModuleCapacity** - Kapazitäts-Prüfung

### 2. Skill Management Nodes - 🔄 PHASE 2 (NÄCHSTER SCHRITT)

- [ ] **WaitForSkillState** - Wartet auf spezifischen Skill-Zustand
  - Parameter: skillName, targetState (SkillStates enum), timeout
  - Pollt oder subscribed auf Skill State
  - Returns: Success wenn State erreicht, Failure bei Timeout
  - Benötigt: RemoteSkill.GetStateAsync()

- [ ] **AbortSkill** - Bricht laufenden Skill ab
  - Parameter: skillName, moduleName
  - Ruft Halt/Abort auf Skill auf
  - Wartet auf Halted State
  - Returns: Success wenn aborted

- [ ] **PauseSkill** - Pausiert Skill (Suspended State)
  - Parameter: skillName, moduleName
  - Ruft Suspend auf Skill auf
  - Returns: Success wenn suspended

- [ ] **ResumeSkill** - Setzt pausierten Skill fort
  - Parameter: skillName, moduleName
  - Ruft Resume/Unsuspend auf
  - Returns: Success wenn wieder Running

- [ ] **RetrySkill** - Wiederholt fehlgeschlagenen Skill
  - Parameter: skillName, maxRetries, backoffMs
  - Reset + Execute mit Retry-Logik
  - Returns: Success wenn erfolgreich

- [ ] **DetermineSkillParameters** - Berechnet Skill-Parameter dynamisch
  - Parameter: skillName, productContext (ProductID, ProductType, etc.)
  - Liest CapabilityDescription, Skill Parameter Definitions
  - Mappt Product Context zu Skill Parameters
  - Returns: Success mit berechneten Parametern

- [ ] **UpdateInventory** - Aktualisiert Inventar nach Skill
  - Parameter: skillName, effects (aus SkillResponse)
  - Liest FinalResultData für ProductID, SlotID
  - Updated Context Storage-State
  - Optional: Sendet InventoryMessage via MQTT

### 3. Messaging Nodes - ⏳ PHASE 3

- [ ] **ReadMqttSkillRequest** - Liest SkillRequest von MQTT
  - Topic: `/Modules/{ModuleID}/SkillRequest/`
  - Parst Action-Element aus InteractionElements
  - Speichert im Context: ActionTitle, Status, InputParameters, Preconditions
  - Returns: Success mit SkillRequest

- [ ] **SendSkillResponse** - Sendet SkillResponse via MQTT
  - Topic: `/Modules/{ModuleID}/SkillResponse/`
  - Parameter: conversationId, ActionState, FinalResultData (optional)
  - Erstellt I4.0 Message Frame
  - Returns: Success wenn gesendet

- [ ] **ReceiveOfferMessage** - Empfängt Angebote (Planning Agent)
  - Aus specs.json
  - Sammelt Offers während Bidding-Phase

---

## Priorität 2: Extended Execution Logic - ⏳ PHASE 4

### 4. Constraint Nodes

- [ ] **RequiresMaterial** - Prüft Material-Verfügbarkeit
  - Parameter: itemId, quantity, moduleId
  - Nutzt ReadStorage oder CheckInventory
  - Returns: Success wenn genug Material

- [ ] **RequiresTool** - Tool-Constraints (aus specs.json)
  - Integriert Tool-Verfügbarkeit

- [ ] **ModuleReady** - Aggregierte Readiness-Prüfung
  - Kombiniert: CheckReadyState, CheckErrorState, CheckLockedState(false), CheckStartupSkillStatus
  - Returns: Success nur wenn alle Checks erfolgreich

- [ ] **ProductMatchesOrder** - Prüft ob richtiges Produkt geladen
  - Parameter: expectedProductType, expectedProductID, slotId
  - Vergleicht mit Storage Content
  - Returns: Success bei Match

- [ ] **ProcessParametersValid** - Validiert Prozessparameter
  - Parameter: paramConstraints (Dict), actualParams (Dict)
  - Prüft Ranges, Types, Required Values
  - Returns: Success wenn alle Constraints erfüllt

- [ ] **ResourceAvailable** - Darf Prozess ausgeführt werden?
- [ ] **SafetyOkay** - Sicherheits-Constraints
- [ ] **RequireNeighborAvailable** - Nachbar verfügbar?

---

## Priorität 3: Advanced Monitoring & Events - ⏳ PHASE 5

### 6. State Monitoring Nodes

- [ ] **RefreshStateMessage** - Aktualisiert alle Modul-States
  - Liest: Ready, Locked, Errors, Inventory, Neighbors
  - Aggregiert in State Summary
  - Sendet StateSummary via MQTT
  - Returns: Success mit State

### 7. Event Nodes (Reactive)

- [ ] **OnSkillStateChanged** - Event-Trigger bei Skill State Change
  - Parameter: skillName, targetState (optional)
  - Subscribed auf OPC UA State Changes
  - Triggert Child-Node wenn State erreicht
  - Decorator/Condition Node

- [ ] **OnInventoryChanged** - Event bei Inventory-Änderung
  - Parameter: itemId (optional), storageComponent
  - Subscribed auf Storage Monitoring Variables
  - Triggert bei Änderung

- [ ] **OnNeighborChanged** - aus specs.json
- [ ] **OnNodeChanged** - Generic OPC UA Subscription

---

## 📊 Implementierungs-Reihenfolge (AKTUALISIERT)

### ✅ Phase 0: Infrastructure (ABGESCHLOSSEN)
1. ✅ MqttLogger implementiert
2. ✅ Trees bereinigt (39 SendLogMessage Nodes entfernt)

### ✅ Phase 1: Core Monitoring (ABGESCHLOSSEN) ✨
1. ✅ CheckReadyState
2. ✅ CheckErrorState
3. ✅ CheckLockedState
4. ✅ MonitoringSkill
5. ✅ **Tests:** Kompiliert und läuft mit Init_and_ExecuteSkill Tree
6. ✅ **Dokumentation:** In TODO-Liste aktualisiert

### 🔄 Phase 2: Skill Control (JETZT - IN ARBEIT)
5. [ ] WaitForSkillState
6. [ ] AbortSkill
7. [ ] PauseSkill
8. [ ] ResumeSkill
9. [ ] RetrySkill
10. [ ] **Tests:** Unit Tests + Integration Test
11. [ ] **Dokumentation:** SKILL_NODES.md erstellen

### ⏳ Phase 3: Messaging Integration
9. [ ] ReadMqttSkillRequest
10. [ ] SendSkillResponse
11. [ ] UpdateInventory
12. [ ] **Tests:** MQTT Integration Tests
13. [ ] **Dokumentation:** MESSAGING_NODES.md

### ⏳ Phase 4: Constraints
12. [ ] RequiresMaterial
13. [ ] ModuleReady
14. [ ] ProductMatchesOrder
15. [ ] ProcessParametersValid
16. [ ] **Tests:** Constraint Logic Tests
17. [ ] **Dokumentation:** CONSTRAINT_NODES.md

### ⏳ Phase 5: Advanced Features
16. [ ] DetermineSkillParameters
17. [ ] RefreshStateMessage
18. [ ] OnSkillStateChanged
19. [ ] CheckInventory erweitert
20. [ ] Weitere Monitoring Nodes aus specs.json
21. [ ] **Tests:** End-to-End Tests
22. [ ] **Dokumentation:** Vollständige API Docs

---

## 🎯 Erfolgs-Kriterien

### ✅ Phase 1 Erfolgreich wenn:
- [x] Alle 4 Core Monitoring Nodes kompilieren
- [x] Nodes in NodeRegistry registriert
- [x] Init_and_ExecuteSkill Tree läuft erfolgreich
- [x] Keine Compiler-Fehler
- [x] MqttLogger sendet automatisch Logs

### 🔄 Phase 2 Erfolgreich wenn:
- [ ] Alle 5 Skill Control Nodes kompilieren
- [ ] WaitForSkillState kann auf State Changes warten
- [ ] AbortSkill kann laufende Skills stoppen
- [ ] PauseSkill/ResumeSkill funktionieren
- [ ] RetrySkill mit Backoff-Logic
- [ ] Test-Tree für Skill-Lifecycle

### ⏳ Minimal Viable Execution Agent kann (nach Phase 3):
1. OPC UA Verbindung aufbauen
2. Modul-Readiness prüfen (Ready, No Error, Not Locked)
3. SkillRequest von MQTT lesen
4. Preconditions validieren (Material, Tools)
5. Skill ausführen mit Parametern
6. Auf Skill Completion warten
7. SkillResponse zurücksenden
8. Inventar aktualisieren
9. Fehler loggen und behandeln

---

## 📁 Dateien die angelegt wurden/werden

### ✅ Phase 1 (Erstellt):
```
MAS-BT/
├── BehaviorTree/
│   └── Nodes/
│       └── MonitoringNodes.cs              [✅ NEU - Phase 1]
├── Services/
│   └── MqttLogger.cs                       [✅ NEU - Infrastructure]
└── Trees/
    ├── Init_and_ExecuteSkill.bt.xml        [✅ BEREINIGT - 53% kleiner]
    └── ModuleInitializationTest.bt.xml     [✅ BEREINIGT - 48% kleiner]
```

### 🔄 Phase 2 (In Arbeit):
```
MAS-BT/
├── BehaviorTree/
│   └── Nodes/
│       └── SkillControlNodes.cs            [🔄 NEU - Phase 2]
└── Trees/
    └── Examples/
        └── SkillLifecycleTest.bt.xml       [🔄 NEU - Test Tree]
```

### ⏳ Zukünftig:
```
MAS-BT/
├── Nodes/
│   ├── Messaging/
│   │   ├── ReadMqttSkillRequestNode.cs     [⏳ NEU - Phase 3]
│   │   └── SendSkillResponseNode.cs        [⏳ NEU - Phase 3]
│   ├── Constraints/
│   │   ├── RequiresMaterialNode.cs         [⏳ NEU - Phase 4]
│   │   ├── ModuleReadyNode.cs              [⏳ NEU - Phase 4]
│   │   └── ProductMatchesOrderNode.cs      [⏳ NEU - Phase 4]
│   └── Events/
│       ├── OnSkillStateChangedNode.cs      [⏳ NEU - Phase 5]
│       └── OnInventoryChangedNode.cs       [⏳ NEU - Phase 5]
└── tests/
    └── Nodes/
        ├── MonitoringNodesTests.cs          [⏳ NEU]
        ├── SkillControlNodesTests.cs        [⏳ NEU]
        └── ConstraintNodesTests.cs          [⏳ NEU]
```

---

## 🚀 Nächste Schritte (AKTUALISIERT)

1. ✅ Phase 1 abgeschlossen
2. ✅ TODO-Liste aktualisiert
3. 🔄 **JETZT: Phase 2 starten** - Skill Control Nodes implementieren
4. [ ] Tests für Phase 2 schreiben
5. [ ] SKILL_NODES.md Dokumentation erstellen
6. [ ] Integration Test mit erweiterten Trees
7. [ ] Phase 3-5 nach Bedarf

---

## 📊 Projekt-Statistik

- **Nodes implementiert:** 19 (14 bestehend + 4 neu + 1 MqttLogger)
- **Trees bereinigt:** 3 (39 SendLogMessage Nodes entfernt)
- **Code-Reduktion:** ~50% in Trees
- **Compile-Status:** ✅ 0 Errors, 5 Warnings (NuGet)
- **Test-Status:** ✅ Init_and_ExecuteSkill Tree läuft erfolgreich
- **Phase 1 Nodes:** 4/4 ✅
- **Noch zu implementieren:** ~30 Nodes aus specs.json


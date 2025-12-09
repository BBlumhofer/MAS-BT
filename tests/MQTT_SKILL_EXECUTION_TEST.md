# MQTT Skill Execution Test

## Übersicht
Dieser Test demonstriert die vollständige Skill-Ausführung über MQTT SkillRequest/SkillResponse Messages zwischen Planning Agent und Execution Agent.

## Implementierte Nodes (Phase 3)

### ✅ ReadMqttSkillRequest
- **Funktion:** Liest Action von Planning Agent via MQTT
- **Topic:** `/Modules/{ModuleID}/SkillRequest/`
- **Returns:** Success mit Action oder Running (wartet auf Message)
- **Context Output:** 
  - `CurrentAction` (SubmodelElementCollection)
  - `ActionTitle`, `ActionStatus`, `MachineName`
  - `InputParameters` (Dictionary<string, string>)
  - `ConversationId`, `RequestSender`

### ✅ SendSkillResponse
- **Funktion:** Sendet ActionState Update an Planning Agent
- **Topic:** `/Modules/{ModuleID}/SkillResponse/`
- **Parameter:** `ModuleId`, `ActionState`, `FrameType` (consent/update/refuse)
- **Context Input:** `ConversationId`, `RequestSender`, `ActionTitle`, `FinalResultData`

### ✅ UpdateInventoryFromAction
- **Funktion:** Aktualisiert Inventar nach Action-Completion
- **Quelle:** Action.FinalResultData oder Action.Effects
- **Optional:** Publiziert InventoryMessage via MQTT

### ✅ SendStateMessage
- **Funktion:** Publiziert Modulzustände via MQTT
- **Topic:** `/Modules/{ModuleID}/State/`
- **Inhalt:** ModuleLocked, ModuleReady, HasError

### ✅ WaitForMessage (Generic)
- **Funktion:** Wartet auf eingehende I4.0 Message mit Filterung
- **Filter:** ExpectedType, ExpectedSender
- **Timeout:** Konfigurierbar

## Test-Workflow

### 1. Vorbereitung

**MQTT Broker starten:**
```bash
# Mosquitto Broker
mosquitto -v

# Oder mit Docker
docker run -it -p 1883:1883 eclipse-mosquitto
```

**Python Dependencies installieren:**
```bash
pip install paho-mqtt
```

### 2. Execution Agent starten

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT
dotnet run -- Trees/Examples/ActionExecutionTest.bt.xml
```

**Was der Execution Agent tut:**
1. ✅ Verbindet zu MQTT Broker (`localhost:1883`)
2. ✅ Verbindet zu OPC UA Server (ScrewingStation)
3. ✅ Lockt das Modul
4. ✅ Führt StartupSkill aus
5. 🔄 **Wartet in Loop auf SkillRequest Messages**

### 3. SkillRequest senden (manueller Test)

**Terminal 2 - Python Publisher:**
```bash
cd /home/benjamin/AgentDevelopment/MAS-BT/tests
python3 mqtt_skill_request_publisher.py
```

**Was das Skript tut:**
- Sendet SkillRequest mit Action "RetrieveToPortLogistic"
- Subscribed zu SkillResponse Topic
- Zeigt alle Responses an

### 4. Erwarteter Ablauf

```
Planning Agent (Python)          Execution Agent (C#)
       |                                 |
       |  SkillRequest (request)         |
       |-------------------------------->| 1. ReadMqttSkillRequest empfängt Action
       |                                 | 2. Speichert Action im Context
       |                                 |
       |  SkillResponse (consent)        |
       |<--------------------------------| 3. SendSkillResponse: ActionState=Starting
       |                                 |
       |                                 | 4. CheckPreconditions (Ready, NoError)
       |                                 | 5. ExecuteSkill (RetrieveToPortLogistic)
       |                                 |
       |  SkillResponse (update)         |
       |<--------------------------------| 6. SendSkillResponse: ActionState=Running
       |                                 |
       |                                 | 7. WaitForSkillState (Ready)
       |                                 | 8. MonitoringSkill
       |                                 | 9. UpdateInventoryFromAction
       |                                 |
       |  SkillResponse (update)         |
       |<--------------------------------| 10. SendSkillResponse: ActionState=Completed
       |                                 |
       |  StateMessage (inform)          |
       |<--------------------------------| 11. SendStateMessage (ModuleLocked, Ready)
       |                                 |
       |                                 | 12. Zurück zu Schritt 1 (Loop)
```

### 5. Expected Output

**Execution Agent Console:**
```
✅ ReadMqttSkillRequest: Received Action 'RetrieveToPortLogistic' with status 'planned'
✅ SendSkillResponse: Sent ActionState 'Starting' to 'Module2_Planning_Agent'
✅ CheckReadyState: Module 'ScrewingStation' ready state: True
✅ CheckErrorState: Module 'ScrewingStation' has error: False
✅ SendSkillResponse: Sent ActionState 'Running' to 'Module2_Planning_Agent'
✅ ExecuteSkill: Starting skill 'RetrieveToPortLogistic'
✅ WaitForSkillState: Skill 'RetrieveToPortLogistic' reached state 'Ready'
✅ UpdateInventoryFromAction: Updated inventory with 3 items
✅ SendSkillResponse: Sent ActionState 'Completed' to 'Module2_Planning_Agent'
✅ SendStateMessage: Published module state to topic '/Modules/Module2/State/'
```

**Python Publisher Console:**
```
✅ SkillRequest erfolgreich gesendet!
📨 Empfangene SkillResponse auf /Modules/Module2/SkillResponse/:
   ActionState: Starting
   ActionTitle: RetrieveToPortLogistic
📨 Empfangene SkillResponse auf /Modules/Module2/SkillResponse/:
   ActionState: Running
   ActionTitle: RetrieveToPortLogistic
📨 Empfangene SkillResponse auf /Modules/Module2/SkillResponse/:
   ActionState: Completed
   ActionTitle: RetrieveToPortLogistic
```

## MQTT Topics

| Topic | Direction | Frame Type | Content |
|-------|-----------|------------|---------|
| `/Modules/Module2/SkillRequest/` | Planning → Execution | `request` | Action mit InputParameters |
| `/Modules/Module2/SkillResponse/` | Execution → Planning | `consent`, `update` | ActionState Updates |
| `/Modules/Module2/State/` | Execution → Broadcast | `inform` | ModuleLocked, ModuleReady, HasError |
| `/Modules/Module2/Inventory/` | Execution → Broadcast | `inform` | Storage Slots (optional) |

## Debugging

**MQTT Monitor in separatem Terminal:**
```bash
# Alle Messages monitoren
mosquitto_sub -h localhost -t '#' -v

# Nur SkillRequest
mosquitto_sub -h localhost -t '/Modules/+/SkillRequest/' -v

# Nur SkillResponse
mosquitto_sub -h localhost -t '/Modules/+/SkillResponse/' -v
```

**Manueller SkillRequest via mosquitto_pub:**
```bash
mosquitto_pub -h localhost -t '/Modules/Module2/SkillRequest/' -m '{
  "frame": {
    "sender": {"identification": {"id": "TestClient"}},
    "receiver": {"identification": {"id": "Module2_Execution_Agent"}},
    "type": "request",
    "conversationId": "test123"
  },
  "interactionElements": [{
    "idShort": "Action001",
    "modelType": "SubmodelElementCollection",
    "value": [
      {"idShort": "ActionTitle", "value": "RetrieveToPortLogistic"},
      {"idShort": "MachineName", "value": "ScrewingStation"}
    ]
  }]
}'
```

## Architektur-Highlights

### ✅ Action als InteractionElement
- **Kein** separates SkillRequest-Objekt
- Action wird direkt als `SubmodelElementCollection` in Message eingebettet
- Planning Agent erstellt Action, Execution Agent führt aus

### ✅ Conversation Tracking
- ConversationId wird über gesamten Request/Response-Zyklus beibehalten
- Das frühere Feld `replyTo` / `messageId` wurde entfernt; Korrelation erfolgt ausschließlich über `conversationId`

### ✅ Non-Blocking Message Reading
- ReadMqttSkillRequest returned `Running` wenn keine Message
- Ermöglicht Retry-Loops ohne Blocking

### ✅ Context-Based Parameter Passing
- Action wird im Context gespeichert
- Alle Nachfolge-Nodes greifen auf Context zu
- Entkopplung zwischen Nodes

## Nächste Schritte

- [ ] **Integration Test:** Planning Agent + Execution Agent zusammen laufen lassen
- [ ] **Preconditions:** EvaluatePreconditions Node implementieren (Phase 4)
- [ ] **Error Handling:** Refuse-Response bei Precondition-Failure
- [ ] **Multi-Action:** Queue mehrerer Actions
- [ ] **Dokumentation:** MESSAGING_NODES.md erstellen

## Status

✅ **Phase 3 komplett implementiert** (4/4 Nodes)
- ReadMqttSkillRequest ✅
- SendSkillResponse ✅  
- UpdateInventoryFromAction ✅
- SendStateMessage ✅

**Build Status:** ✅ MessagingNodes.cs kompiliert fehlerfrei

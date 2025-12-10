# MAS-BT Progress (2025-12-09)

## Latest changes
- Skill preconditions are now evaluated before ExecuteSkill runs: module must be coupled, locked, startup skill running, and custom `InStorage` conditions must resolve against current storages; unmet conditions cause the request to re-queue instead of failing.
- Skill execution no longer refuses when the module is unlocked: ExecuteSkill, Ready-State monitoring, and MQTT dequeueing now requeue the SkillRequest until the lock is reacquired, preventing duplicate error responses.
- Execution queue now persists full SkillRequest envelopes (raw I4.0 message + metadata) so responses always carry the correct conversation/product ID and sender/receiver pair.
- Action queue snapshots are now published to `/Modules/{ModuleId}/ActionQueue/` whenever the queue changes; each message contains metadata for every pending action.
- Messaging serializer now uses BaSyx FullSubmodelElementConverter + JsonStringEnumConverter; interactionElements include `value` fields again (logs, state, inventory, neighbor, skill messages).
- SkillRequest parsing builds a BaSyx `SubmodelElementCollection` and `AasSharpClient.Models.Action`; input parameters are auto-typed (strings like "true" become bool) to avoid OPC UA BadTypeMismatch.
- Log messages sent via `SendLogMessageNode` now carry populated values; MQTT payload matches the AAS-Sharp-Client examples.
- SubTree expansion and Groot compatibility fixes remain in place (decorator child count, TreeNodesModel ports for ExecuteSkill, Repeat num_cycles).

## Current focus
- Queue and Preconditions design: enqueue SkillRequests, consent/refuse with queue-full handling; preconditions SMC (InStorage) with SlotContentType/SlotValue; ActionUpdate on precondition retries.
- Recovery and monitoring: continuous health monitor for lock/startup/error, trigger RecoverySequence; improve Couple/Startup retries and timeouts.
- Messaging paths: finalize SendMessage/WaitForMessage and inventory/neighbor readers using the unified serializer; add MQTT integration tests.

## Known issues and warnings
- OPC UA KeepAlive can report Bad transiently; SDK reconnect handles it but monitor logs.
- Build warnings: NU1510 for System.Text.Json (pruning), CS0108 in ProcessParametersValidNode (hides Equals).
- Occasional skill state values from server (e.g., 17) still need mapping/logging for clarity.

## Verification
- `dotnet build MAS-BT/MAS-BT.csproj -c Debug` succeeds (warnings above).
- Example run: `dotnet run --Examples/ActionExecutionTest.bt.xml` (used during latest checks).
 - Example run: `dotnet run -- Trees/PlanningAgent.bt.xml` or `dotnet run -- Examples/ActionExecutionTest.bt.xml` (used during latest checks). Note: `PlanningAgent` no longer loads a demo ProductionPlan; plans should be provided externally.
- Lock-aware queue scenario: start the agent, intentionally release the module lock, publish a skill via `tests/mqtt_skill_request_publisher.py`, and observe the request waiting in the queue until the lock is granted again (no refusal/error spam).
- SkillRequest publisher (`tests/mqtt_skill_request_publisher.py`) exercises BaSyx parsing and typed parameter handling.

## Next steps (shortlist)
1) Implement and wire Execution-Queue with precondition-aware dequeue/skip/retry; add ActionUpdate messaging for retries.
2) Add ContinuousHealthCheck/RecoverySequence monitor branch in example trees; extend Recovery tests.
3) Finish messaging nodes (SendMessage, WaitForMessage, Neighbor/Inventory readers) and add MQTT integration tests.
4) Optional: clean build warnings (NU1510, CS0108).

## Preconditions — Implementation & Schritte (2025-12-09)

- **Ziel:** Präconditions vor Skill-Start auswerten (Standard: `Coupled`, `StartupSkill running`, `Locked`; Action-spezifisch: `InStorage`).

- **Durchgeführte Schritte:**
	- `Preconditions`-Datenmodell erweitert in `AAS-Sharp-Client/Models/SubmodelCollections.cs` (neue `Preconditions`-Collection, `PreconditionBase`, `StoragePrecondition`).
	- `ReadMqttSkillRequestNode` angepasst: kopiert deklarierte Precondition-SMCs in das erstellte `Action`-Objekt.
	- `SkillPreconditionChecker` implementiert (`MAS-BT/Services/SkillPreconditionChecker.cs`) zur Evaluation von Standard- und Action-Preconditions (inkl. `InStorage`).
	- `ExecuteSkillNode` erweitert: Evaluierung der Preconditions vor Start; bei Nichterfüllung wird die Anfrage requeued statt hart zu fehlschlagen.
	- `ExecuteSkillNode` erweitert: Evaluierung der Preconditions vor Start; bei Nichterfüllung wird die Anfrage requeued und an das Queue-Ende verschoben (Backoff gesetzt) statt hart zu fehlschlagen. `SendSkillResponse` unterdrückt ERROR-Responses für requeued Actions, so verbleiben sie in der Queue.
	- Unit-Tests / Hilfsparser (z.B. `SkillRequestQueueTests`) aktualisiert; Build geprüft (`dotnet build`) — grüner Build nach Anpassungen.

- **Verifikation:**
	- Beispiel-Run zeigte: SkillRequest wird geparsed, in die Queue gestellt, `CheckReadyState` prüft Lock/Errors, `ExecuteSkill` führt Precondition-Check aus und requeues wenn `InStorage` nicht erfüllt ist.
	- Logs zeigen wiederholtes Requeue bei fehlendem Storage-Eintrag (erwartetes Verhalten).

## Offene Execution-Agent TODOs (Analyse)

- **Hohe Priorität:**
	- ActionUpdate Messaging: Bei jedem Preconditions-Retry soll eine `ActionUpdate` mit dem Grund `preconditions not satisfied` verschickt werden (inkl. SlotContentType/SlotValue). (nicht implementiert)
	- Retry-Strategie / Backoff: Queue verwendet jetzt `PreconditionBackoffStartMs` (default 5000ms) und inkrementelle Retries (exponentiell). Implementiert: `MaxPreconditionRetries` (default 5) bricht nach N erfolglosen Versuchen ab und lässt die Action in einen ERROR-Zustand übergehen.
	- Skip/Dequeue-Strategie: Dispatcher sollte nicht-startbare Jobs überspringen und die nächste startfähige Action starten; nach jedem Durchlauf blockierte Jobs erneut prüfen. (teilweise vorhanden, noch Feinschliff nötig)

- **Mittlere Priorität:**
	- Integrationstests: End-to-End Tests für Precondition-Szenarios (InStorage vorhanden / fehlt, Coupling fehlt, StartupSkill halted). (nicht implementiert)
	- Telemetrie & Queue-Metriken: Queue-Länge, älteste Wartezeit, Anzahl Requeues pro Action als Telemetrie/Log. (nicht implementiert)
	- API für Cancel/Remove: Planning-Agent muss Queue-Einträge per ActionId/ConversationId entfernen können (Rückmeldung via ActionUpdate). (nicht implementiert)

- **Niedrige Priorität / Nice-to-have:**
	- Standardverhalten bei fehlendem `SlotContentType`: definieren (aktuell `Unknown` — evtl. Default `ProductId`). (Diskussion/Anpassung empfohlen)
	- Logging-Verbesserungen: prägnantere ActionUpdate-Gründe, mapping von SlotContentType-Enums in Doku.
	- Code Cleanup: Warnings (NU1510) prüfen, ungenutzte Felder bereinigen.

Wenn du möchtest, implementiere ich als nächstes die `ActionUpdate`-Publizierung bei Preconditions-Retry und eine einfache Retry-Limit/Backoff-Strategie im `SkillRequestQueue` (empfohlen, verhindert Spam/infinite requeues). Sage mir welches Verhalten (max retries, initial backoff) du bevorzugst, dann setze ich das um.

## References
- Detailed node docs: `CONFIGURATION_NODES.md`, `MONITORING_AND_SKILL_NODES.md`.
- Roadmap and historical notes: `EXECUTION_AGENT_TODO.md`.
- Specs: `specs.json`.

## Inventory MQTT — Status (2025-12-09)

- **Kurzfassung:** Storage-Änderungen werden jetzt über den Skill-Client (RemoteServer) erfasst und über MQTT veröffentlicht. Der Publisher verwendet das Topic `/Modules/{ModuleId}/Inventory/`.
- **Implementierung:** Die Klasse `StorageMqttNotifier` wird während der Modulverbindung in `ConnectToModule` registriert. Sie abonniert Storages/Slots über `RemoteServer.SubscriptionManager`, loggt eingehende Änderungen und veröffentlicht ein `InventoryMessage`-Payload an das oben genannte Topic.
- **Diagnose:** Beim Eintreten einer Speicheränderung werden Logeinträge erzeugt, z. B. "detected storage change for module {ModuleId} — publishing to topic /Modules/{ModuleId}/Inventory/". Fehler beim Publish werden ebenfalls geloggt.
- **Verifikation:**
	- Starte das Beispiel-Tree oder den Agenten, z. B. `dotnet run -- Examples/ActionExecutionTest.bt.xml` im `MAS-BT`-Ordner.
	- Prüfe die Agent-Logs auf Meldungen wie: `published storage change to topic /Modules/{module}/Inventory/` oder auf Warnungen über fehlende Subscriptions.
	- Prüfe deinen MQTT-Broker (z. B. mit `mosquitto_sub`):

```bash
# Beispiel: abonniere Inventory-Topic für Modul `TestAgent`
mosquitto_sub -t "/Modules/TestAgent/Inventory/#" -v
```

	- Wenn keine Nachrichten ankommen: prüfe die Agent-Logs auf Publish-Fehler (Connectivity/Authentication) und die Ausgabe von `StorageMqttNotifier` (Anzahl Subscriptions, Null-Guards).

- **Bekannte Punkte:**
	- `EnableStorageChangeMqttNode` ist ein No-Op; Registrierung erfolgt in `ConnectToModule`.
	- Topic wurde von früheren Testwerten zurück auf `/Modules/{ModuleId}/Inventory/` korrigiert.

- **Update (Debounce & Logs):**
	- **Coalescing / Debounce:** Storage-Änderungen werden jetzt kohärent gebündelt (Debounce ~150 ms) in `StorageMqttNotifier`. Das verhindert schnelle Mehrfach-Publishes bei sequenziellen OPC UA-Ävents — nur eine zusammengefasste `InventoryMessage` pro Storage-Burst wird gesendet.
	- **Separates Log-Topic:** Diagnose-/Kurztexte (~LogMessage) werden nicht mehr als Inventory-Payload gesendet, sondern separat an `/Modules/{ModuleId}/Logs/` publiziert.
	- **Tuning:** Debounce-Wert (`_debounceMs`) ist in `StorageMqttNotifier` gesetzt (Default 150 ms). Erhöhung auf 300–500 ms reduziert Publishes bei hochfrequenten Updates; erniedrigen wenn Latenz wichtiger ist.

- **Lock-Retry Diagnose:**
	- `LockResourceNode` enthält nun zusätzliche Debug-Logs (enableRetry, TimeoutSeconds, RetryDelaySeconds, deadline, attempt). Diese helfen zu erkennen, warum beim Initialisieren kein Retry ausgelöst wurde (z. B. Modul bereits durch Dritten gelockt).
	- Aktuelles Verhalten: `RemoteModule.LockAsync()` bleibt eine einzelne (immediate) Lock-Attempt; Retry/Backoff liegt in `LockResourceNode` (Tree-Node). Falls erwünscht, können wir `LockResourceNode` so anpassen, dass bei `null` als Ergebnis (transiente Fehler) ebenfalls Retry-Verhalten ausgeführt wird.

- **Verifikation (aktualisiert):**
	- Starte den Agenten mit dem Beispiel-Tree:

```bash
dotnet run -- Examples/ActionExecutionTest.bt.xml
```

	- Achte auf je Storage-Burst genau eine `published inventory to topic /Modules/<Module>/Inventory/`-Zeile und eine `published log to topic /Modules/<Module>/Logs/`.
	- Prüfe Lock-Diagnose-Logs bei Initialisierung, z. B. `LockResource: timeoutSeconds=...` und `LockResource: attempt=...`.


- **Nächste Schritte:**
	1. Falls Logs zeigen, dass Publish-Versuche fehlschlagen: Broker-Zugangsdaten, Topic-ACLs und `MessagingClient`-Verbindung prüfen.
	2. Optional: MQTT-Integrationstests hinzufügen, die Broker (lokal/CI) emulieren und auf `Inventory`-Nachrichten prüfen.


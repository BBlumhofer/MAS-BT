# MAS-BT Progress (2025-12-09)

## Latest changes
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
- SkillRequest publisher (`tests/mqtt_skill_request_publisher.py`) exercises BaSyx parsing and typed parameter handling.

## Next steps (shortlist)
1) Implement and wire Execution-Queue with precondition-aware dequeue/skip/retry; add ActionUpdate messaging for retries.
2) Add ContinuousHealthCheck/RecoverySequence monitor branch in example trees; extend Recovery tests.
3) Finish messaging nodes (SendMessage, WaitForMessage, Neighbor/Inventory readers) and add MQTT integration tests.
4) Optional: clean build warnings (NU1510, CS0108).

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

- **Nächste Schritte:**
	1. Falls Logs zeigen, dass Publish-Versuche fehlschlagen: Broker-Zugangsdaten, Topic-ACLs und `MessagingClient`-Verbindung prüfen.
	2. Optional: MQTT-Integrationstests hinzufügen, die Broker (lokal/CI) emulieren und auf `Inventory`-Nachrichten prüfen.


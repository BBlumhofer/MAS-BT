Inventory MQTT Integration

Ziel
- Publizieren von Storage-/Inventory-Änderungen über MQTT, unabhängig von `ActionTitle`, damit andere Holons/Module aktuelle Lagerstände empfangen können.

Topic
- `/{Namespace}/{ModuleId}/Inventory` — Inventory-Updates werden pro Modul auf diesem Topic veröffentlicht.

MessageType
- Typischerweise `inventoryUpdate`.

Payload (Kurzform)
- Enthält `StorageUnits` (detaillierte Struktur) und zusätzlich eine kompakte `InventorySummary` **innerhalb** von `StorageUnits`:
  - `free` (int)
  - `occupied` (int)

Registrierung
- Abhängig vom Setup werden Storage-/Slot-Änderungen aus der Execution-Seite gesammelt und als MQTT-Nachricht auf `/{Namespace}/{ModuleId}/Inventory` publiziert.

Logging & Diagnose
- Bei Speicheränderungen sollten Diagnose-Logs sichtbar sein, die auf Publishes an `/{Namespace}/{ModuleId}/Inventory` hinweisen.
- Publish-Fehler werden ebenfalls geloggt.

Verifikation (kurz)
1. Starte das Beispiel-Tree: `dotnet run -- Examples/ActionExecutionTest.bt.xml` im `MAS-BT`-Ordner.
2. Prüfe die Agent-Logs auf die oben genannten Logzeilen.
3. Abonniere das Topic lokal (Beispiel):

```bash
mosquitto_sub -t "/phuket/P102/Inventory" -v
```

4. Wenn keine Nachrichten ankommen: prüfe Broker-Verbindung, Topic-ACLs und die Agent-Logs auf Publish-Fehler.

Bekannte Punkte
- Dieses Dokument verwendet das aktuelle Topic-Schema `/{Namespace}/{ModuleId}/Inventory`. Ältere Beispiele mit `/Modules/...` sind veraltet.

Nächste Schritte
- Falls Logs zeigen, dass Publish-Versuche fehlschlagen: Broker-Zugangsdaten, Topic-ACLs und `MessagingClient`-Verbindung prüfen.
- Optional: MQTT-Integrationstests hinzufügen, die Broker (lokal/CI) emulieren und auf `Inventory`-Nachrichten prüfen.

Debounce / Coalescing
- Schnelle aufeinanderfolgende Storage-Änderungen sollten zusammengefasst werden (Debounce/Coalescing), damit nicht pro Slot-Property eine MQTT-Nachricht entsteht.

Log-Topic
- Kurze Diagnose-/Log-Nachrichten werden **nicht** als Inventory-Payload gesendet.

Dispatcher-Aggregation
- Der DispatchingAgent abonniert Inventory über Wildcard: `/{Namespace}/+/Inventory`.
- Er hält pro Modul den zuletzt bekannten `InventorySummary` (free/occupied) und bildet daraus eine Gesamtsumme.
- Diese Gesamtsumme wird bei der Dispatcher-Registration zum Namespace (`/{Namespace}/register`) als `InventorySummary` mitgesendet.

Lock-Retries & Diagnose
- `LockResourceNode` wurde mit zusätzlichen Debug-Logs erweitert (enableRetry, TimeoutSeconds, RetryDelaySeconds, deadline, attempt) um zu zeigen, ob und wie die Tree-Node Retries ausführt. `RemoteModule.LockAsync()` bleibt unverändert – es führt genau einen Lock-Attempt aus; Retry-Policy liegt in der Tree-Node.

Verifikation: praktische Schritte
- Beobachte Konsole/Logs beim Ausführen des Beispiel-Trees; pro Storage-Burst solltest du genau 1 Inventory-Publish sehen. Falls du weiterhin mehrfach Publishes beobachtest, prüfe die Storage-Keys in den Logs (Module/Storage-Name) — ggf. sind mehrere unterschiedliche Storages betroffen.


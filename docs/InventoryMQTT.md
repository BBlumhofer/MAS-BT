Inventory MQTT Integration

Ziel
- Publizieren von Storage-/Inventory-Änderungen über MQTT, unabhängig von `ActionTitle`, damit andere Holons/Module aktuelle Lagerstände empfangen können.

Topic
- `/Modules/{ModuleId}/Inventory/` — `StorageMqttNotifier` veröffentlicht an dieses Topic.

Registrierung
- `ConnectToModule` registriert zur Laufzeit eine `StorageMqttNotifier`-Instanz, die über den `RemoteServer.SubscriptionManager` Storages/Slots abonniert.

Logging & Diagnose
- Bei Speicheränderungen erzeugt der Notifier Diagnose-Logs wie:
  - `detected storage change for module {ModuleId} — publishing to topic /Modules/{ModuleId}/Inventory/`
  - `published storage change to topic /Modules/{module}/Inventory/`
- Publish-Fehler werden ebenfalls geloggt.

Verifikation (kurz)
1. Starte das Beispiel-Tree: `dotnet run -- Examples/ActionExecutionTest.bt.xml` im `MAS-BT`-Ordner.
2. Prüfe die Agent-Logs auf die oben genannten Logzeilen.
3. Abonniere das Topic lokal (Beispiel):

```bash
mosquitto_sub -t "/Modules/TestAgent/Inventory/#" -v
```

4. Wenn keine Nachrichten ankommen: prüfe Broker-Verbindung, Topic-ACLs und die Agent-Logs auf Publish-Fehler.

Bekannte Punkte
- `EnableStorageChangeMqttNode` ist ein informatives/No-Op-Node; die eigentliche Registrierung erfolgt in `ConnectToModule`.
- Topic wurde von früheren Testwerten zurück auf `/Modules/{ModuleId}/Inventory/` korrigiert.

Nächste Schritte
- Falls Logs zeigen, dass Publish-Versuche fehlschlagen: Broker-Zugangsdaten, Topic-ACLs und `MessagingClient`-Verbindung prüfen.
- Optional: MQTT-Integrationstests hinzufügen, die Broker (lokal/CI) emulieren und auf `Inventory`-Nachrichten prüfen.

Debounce / Coalescing
- `StorageMqttNotifier` fasst schnelle aufeinanderfolgende OPC UA-Änderungen pro Storage zusammen (Debounce / Coalescing). Standardwert: `_debounceMs = 150` ms.
- Wirkung: Bei mehreren schnellen Events (z. B. LastText + CarrierID + Slot update) wird nur eine kombinierte `InventoryMessage` an `/Modules/{ModuleId}/Inventory/` gesendet und eine separate `LogMessage` an `/Modules/{ModuleId}/Logs/`.
- Tuning: Erhöhe `_debounceMs` auf 300–500 ms, wenn dein Hardware sehr viele Events in kurzer Zeit sendet; verringere es, wenn du geringere Latenz zwischen Event und Publish brauchst.

Log-Topic
- Kurze Diagnose-/Log-Nachrichten werden **nicht** als Inventory-Payload gesendet. Stattdessen veröffentlicht `StorageMqttNotifier` `LogMessage`-Elemente an `/Modules/{ModuleId}/Logs/`.

Lock-Retries & Diagnose
- `LockResourceNode` wurde mit zusätzlichen Debug-Logs erweitert (enableRetry, TimeoutSeconds, RetryDelaySeconds, deadline, attempt) um zu zeigen, ob und wie die Tree-Node Retries ausführt. `RemoteModule.LockAsync()` bleibt unverändert – es führt genau einen Lock-Attempt aus; Retry-Policy liegt in der Tree-Node.

Verifikation: praktische Schritte
- Beobachte Konsole/Logs beim Ausführen des Beispiel-Trees; pro Storage-Burst solltest du genau 1 Inventory-Publish sehen. Falls du weiterhin mehrfach Publishes beobachtest, prüfe die Storage-Keys in den Logs (Module/Storage-Name) — ggf. sind mehrere unterschiedliche Storages betroffen.


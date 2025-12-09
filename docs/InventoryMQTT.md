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

# Startup & MQTT Diagnostics

Dieses Dokument beschreibt empfohlene Start-Varianten für Agenten, wie MQTT-ClientIds gebildet werden, und eine Troubleshooting-Checkliste für typische Fehler (insbesondere Disconnects durch duplicate ClientIds).

## Hintergrund
- Der MQTT-ClientId ist broker-seitig der eindeutige Identifikator für eine Verbindung. Wenn zwei Clients dieselbe ClientId benutzen, schließt der Broker die ältere Verbindung.
- In diesem Projekt werden ClientIds aus konfigurierbaren Platzhaltern gebildet. Aktuelles Format (empfohlen):

```
{config.Agent.AgentId}_{config.Agent.Role}
```

Damit die Platzhalter korrekt ersetzt werden, müssen die Keys `config.Agent.AgentId` und `config.Agent.Role` im BT-Context vorhanden sein. Der Runner (`Examples/ModuleInitializationTestRunner`) setzt diese Werte jetzt beim Start.

## Start-Varianten

- Einzelner Agent (mit Config-Pfad):

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT
dotnet run -- /path/to/configs/specific_configs/Module_configs/P103/P103_Planning_agent.json
```

- Alle Agenten (Spawner):

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT
dotnet run agents
```

- Spawn mit eigenen Terminals (Debugging):

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT
dotnet run agents --spawn-terminal
```

Empfehlung: Starte zunächst kleine Batches (3–5 Agenten) mit `--spawn-terminal`, beobachte Stabilität und erhöhe dann schrittweise.

## Checks beim Start

- Agent-Terminal (erwartet):

  - `ConnectToMessagingBroker: Using MQTT ClientId <clientId>` — ClientId sollte kein Literal-Placeholder sein.
  - `MessagingClient: Connected to MQTT broker` — ohne unmittelbar folgende `Disconnected`.

- Broker-Log (Mosquitto):

```bash
docker logs -f mosquitto
# oder bei systemd:
sudo journalctl -u mosquitto -f
```

Suche nach:
- korrekte ClientIds wie `P103_Planning_PlanningHolon` bzw. `P103_Execution_ExecutionHolon` (je nach Template)
- KEINEN Literal-String wie `{config.Agent.AgentId}_{config.Agent.Role}`
- Meldungen `Client <id> already connected, closing old connection` → Duplicate ClientId

## Troubleshooting-Checkliste

- Platzhalter nicht ersetzt (Literal `{config...}`):
  - Ursache: `config.Agent.AgentId` oder `config.Agent.Role` nicht im Context gesetzt.
  - Fix: Sicherstellen, dass die Runner/BT-Initialisierung die Keys setzt (fix in `Examples/ModuleInitializationTestRunner.cs` bereits angewendet).

- Duplicate ClientId / ständige Reconnects:
  - Ursache: mehrere Instanzen verwenden dieselbe ClientId.
  - Fix: `AgentId` eindeutig setzen in jeder Config; ClientId-Template nutzen.
    - Empfehlung für Sub-Holons: `{ModuleId}_Planning` und `{ModuleId}_Execution` (z. B. `P103_Planning`, `P103_Execution`).
  - Workaround: Starte in Batches, erhöhe Broker-Logging und prüfe `max_connections`/Limits.

- Broker-Ressourcen bzw. Netzwerkprobleme:
  - Symptom: KeepAlive-Warnungen, abwechselnde Disconnects.
  - Fix: Prüfe Broker-CPU/RAM, Netzwerklast; erhöhe `MQTT.KeepAliveInterval` oder `ReconnectDelay` in Config.

- UAClient KeepAlive-Warnungen:
  - UAClient-Fehler (KeepAlive Bad) können sekundäre Effekte verursachen. Prüfe OPC UA Endpunkte und Netzwerk.

## Empfohlene Diagnoseschritte

1. Starte 3–4 Agenten mit `--spawn-terminal`.
2. Beobachte Terminals und Broker-Log; bestätige korrekte ClientIds.
3. Bei Disconnects: sammle Broker-Logs, kopiere ein Agent-Terminal-Log und `ss -tnp | grep 1883` auf Broker-Host.
4. Falls nötig: erhöhe Broker-Logging, führe warm-up in kleineren Chargen durch.

## Beispielkonfiguration prüfen

Stelle sicher, dass jede Agent-Config mindestens folgende Felder enthält:

```json
"Agent": {
  "AgentId": "P103_Planning",
  "ModuleId": "P103",
  "Role": "PlanningHolon",
  "ModuleName": "ScrewingStation"
}
```

## Weiteres
- Wenn du möchtest, kann ein automatischer Test-Spawn (z. B. 4 Agenten) hier ausgeführt und die Logs gesammelt werden. Kontaktiere mich mit Anzahl und ob `--spawn-terminal` gewünscht ist.

---

Datei: `docs/STARTUP_AND_MQTT.md`

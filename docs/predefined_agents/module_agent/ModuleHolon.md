# Module Holon (Router um Planning- und Execution-Agent)

Der Module Holon kapselt PlanningAgent und ExecutionAgent und tritt nach außen als einziges Modul-Ende auf. Er übernimmt Registrierung, Routing und das Lifecycle-Management der Sub-Holone (Planning/Execution) in eigenen Threads.

## Ziele
- **Single Endpoint nach außen:** Dispatcher sieht nur den Module Holon; interne Agents bleiben verborgen.
- **Registrierung & Heartbeats:** Module Holon meldet Modul-Fähigkeiten/Topology beim Dispatching Agent an und hält die Registrierung aktuell.
- **Routing:** Alle Angebots-/Scheduling-/Booking- und Transport-Nachrichten werden über den Module Holon geleitet und an Planning/Execution weitergereicht.
- **Sub-Holon-Instanziierung:** Module Holon startet PlanningAgent und ExecutionAgent in eigenen Threads/Prozessen und registriert sie auf einem internen Register-Topic.

## Rollen & Topics
- **Extern (Dispatcher-facing, Namespace-basiert):**
  - Registration (ModuleHolon → Dispatcher): `/{Namespace}/DispatchingAgent/register`
  - Offers (Dispatcher → ModuleHolon): `/{Namespace}/DispatchingAgent/Offer`
  - Scheduling: `/{Namespace}/{ModuleId}/ScheduleAction`
  - BookingConfirmation: `/{Namespace}/{ModuleId}/BookingConfirmation`
  - TransportPlan: `/{Namespace}/{ModuleId}/TransportPlan`
  - Capabilities (Modul-Publish): `/{Namespace}/{ModuleId}/Capabilities`
  - Inventory (Modul-Publish): `/{Namespace}/{ModuleId}/Inventory`
- **Intern (Sub-Holon-facing, Module-spezifisch):**
  - Sub-Holons registrieren sich beim ModuleHolon über: `/{Namespace}/{ModuleId}/register` (MessageType: `subHolonRegister`)

## Ablauf (vereinfacht)
1. **Init:** Module Holon lädt Config (AAS-Endpunkte, ModuleId, Namespace, Subholons), verbindet MQTT, liest Nameplate/CapabilityDescription/Neighbors.
2. **Registrierung:** Publiziert eine standardisierte `RegisterMessage` an `/{Namespace}/DispatchingAgent/register` (MessageType: `registerMessage`).
3. **Sub-Holon-Start:** Spawnt PlanningAgent und ExecutionAgent (eigene Threads/Tasks/Prozesse) und wartet auf deren Registrierung über `/{Namespace}/{ModuleId}/register`.
4. **Routing-Loop:**
   - Offer-/Scheduling-/Booking-/Transport-Nachrichten vom Dispatcher → interne Topics → wartet auf Antwort → sendet zurück an Dispatcher (ConversationId bleibt erhalten).
   - Heartbeat/Registration-Refresh in Intervallen.
5. **Health/Recovery:** Falls ein Sub-Holon nicht reagiert, kann der Module Holon Refuse senden oder das Sub-Holon neu starten.

## Behavior-Tree-Skizze
```
Sequence ModuleHolon
  ReadConfig (module_holon.json)
  Bind AgentId/Role/Namespace
  ConnectToMessagingBroker
  ReadShell / ReadCapabilityDescription / ReadNeighbors
  RegisterModule (send to dispatcher)
  SpawnSubHolons (Planning, Execution) in Threads
  WaitForSubHolonRegister (Topic /{Namespace}/{ModuleId}/register)
  SubscribeExternalTopics
  Parallel
    - OfferHandler Loop (dispatcher offer req -> planning -> dispatcher)
    - ScheduleHandler Loop (schedule/book -> planning -> booking confirmation)
    - TransportHandler Loop (transport req -> planning/transport)
    - Heartbeat/RegistrationRefresh Loop
```

## Inventory- und Neighbor-Snapshots
- `SubscribeAgentTopics` abonniert `/{Namespace}/{ModuleId}/Inventory` sowie `/{Namespace}/{ModuleId}/Neighbors` und cached die eingehenden `inventoryUpdate`- bzw. `neighborsUpdate`-Payloads.
- Wichtig: **Inventory wird nicht mehr in der `RegisterMessage` mitgesendet.** Der Dispatcher aggregiert Inventory über das eigene Topic `/{Namespace}/{ModuleId}/Inventory`.
- Für schnelle Übersicht enthält jede Inventory-Nachricht innerhalb von `StorageUnits` zusätzlich ein `InventorySummary` (Properties: `free`, `occupied`).

## Registrierung der Sub-Holone
- Jeder Sub-Holon veröffentlicht nach Start eine Nachricht auf `/{Namespace}/{ModuleId}/register` (Type: `subHolonRegister`).
- Module Holon speichert die Sub-Holon-Endpunkte und nutzt sie für das Routing.
- Startbefehle werden über `SubHolons` im ModuleHolon-Config referenziert (z. B. `P102_Planning_agent`, `P102_Execution_agent`).

## Naming-Konvention (wichtig für Multi-Modul-Tests)
- `Agent.AgentId` muss pro Prozess eindeutig sein, sonst kommt es zu MQTT-Disconnects (duplicate ClientId) und/oder zu Problemen beim Warten auf Sub-Holon-Registrierungen.
- Empfohlen:
  - Planning: `{ModuleId}_Planning` (z. B. `P103_Planning`)
  - Execution: `{ModuleId}_Execution` (z. B. `P103_Execution`)

## Offene Punkte
- Start der Sub-Holone: per ProcessStart vs. Thread/Task (aktuell Thread/Task vorgesehen).
- Fehlerfall: Backoff/Retry beim Spawn und beim internen Routing.
- Security/ACL: Topics ggf. einschränken, falls MQTT-ACLs aktiv sind.

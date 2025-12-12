# Capability Request Routing – aktueller Stand

## Beteiligte Komponenten
- **Product Agent** (`configs/specific_configs/product_agent.json`) sendet `callForProposal` auf `/phuket/ProcessChain`.
- **Dispatching Agent** (`configs/specific_configs/dispatching_agent.json`) empfängt den CfP, analysiert ihn und publiziert pro Fähigkeit eine CfP-Nachricht auf `/phuket/DispatchingAgent/Offer`.
- **ModuleHolon P102** (`configs/specific_configs/Module_configs/P102/P102.json`) lauscht auf `/phuket/DispatchingAgent/Offer` und muss jede CfP an seinen internen Planning Agent weiterreichen.
- **Planning Agent P102** (`configs/specific_configs/Module_configs/P102/P102_Planning_agent.json`) erwartet `callForProposal` auf `/phuket/P102/PlanningAgent/OfferRequest` (sowie auf der Alias-Adresse `/phuket/AssemblyStation/...`) und antwortet mit `proposal`.

## Änderungen am Code (Stand 12.12.)
1. **Alias-Unterstützung für Modul-IDs**
   - `ModuleContextHelper` bietet jetzt `ResolveModuleIdentifiers`, das AgentId, ModuleId und ModuleName zusammenführt. Dadurch können wir gleichzeitig auf `/phuket/P102/...` und `/phuket/AssemblyStation/...` arbeiten.
   - `ForwardCapabilityRequestsNode` veröffentlicht CfPs nun auf *allen* Alias-Topics des Moduls, nicht nur auf einer festen ID (`Nodes/ModuleHolon/ForwardCapabilityRequestsNode.cs`).
   - `SubscribeModuleHolonTopicsNode` und `SubscribePlanningTopicsNode` abonnieren ebenfalls sämtliche Alias-Topics.
   - `ForwardToInternalNode` wurde angepasst, damit auch hier mehrere Ziel-Topics bedient werden können.

2. **Steuerfluss im Angebot-Loop**
   - Die Forwarding-Node liefert `Failure`, solange kein CfP bereitliegt. Die Behavior-Tree-Sequenz fällt so in den kurzen Rückfall-`Wait` und blockiert die Schleife nicht mehr.

3. **Tests**
   - `ModuleMessagingIntegrationTests` prüfen den Publish auf beiden Topics und unterstützen optional reale MQTT-Verbindungen.

## Beobachteter Status in der Laufzeitumgebung
- Nach dem Start (Reihenfolge: `dotnet run dispatching_agent`, `dotnet run P102`, danach Product Agent) trifft der CfP vom Dispatching Agent auf `/phuket/DispatchingAgent/Offer` ein.
- Der ModuleHolon loggt `ForwardCapabilityRequests: queued CfP conversation … (queue=n)` mehrfach hintereinander.
- Erst **mit spürbarer Verzögerung (mehrere Sekunden)** erscheint `ForwardCapabilityRequests: forwarded conversation …` und der CfP erreicht `/phuket/P102/PlanningAgent/OfferRequest`.
- Zwischen mehreren CfPs derselben Konversation liegen jeweils große Zeitabstände; die Queue wächst (queue=1,2,3, …) bevor überhaupt eine Weiterleitung stattfindet.
- Der Planning Agent reagiert nach dem Eintreffen, publiziert aber ebenfalls verspätet.

## Einschätzung der Delay-Ursache
- Die BT-Schleife für Angebote (`ModuleHolon.bt.xml`, `OfferLoop`) enthält eine `Wait`-Kette (150 ms Idle, 200 ms Backoff). Dennoch deuten die Logs darauf hin, dass der `ForwardCapabilityRequestsNode` deutlich seltener getickt wird als erwartet – vermutlich, weil der parallele Registrierungspfad und das Warten auf Sub-Holons viel Zeit benötigt.
- Zusätzlich blockiert `WaitForMessage` im Planning Agent standardmäßig bis zu 10 s. Sobald eine CfP-Welle ansteht, kann das in Kombination mit mehreren CfPs pro Konversation große Verzögerungen erzeugen.

## To‑Do / Offene Punkte
1. **Analyse der Tick-Frequenz**: Während CfP-Spitzen lastet die Modul-Loop offenbar stark. Messpunkte im `OfferLoop` (vor/nach jeder Node) könnten bestätigen, wie viel Zeit zwischen zwei Ticks vergeht.
2. **Frühzeitiges Dequeue**: Statt mehrere CfPs derselben Conversation zu sammeln, sollte jede Nachricht bei der ersten Gelegenheit weitergeleitet werden – ggf. per sofortigem Publish im Callback, statt über `Execute` (Push-basiert).
3. **Timeouts abstimmen**: Die Planning-Agent-`WaitForMessage`-Nodes arbeiten mit 10 s Timeout. Für CfP-Handling könnte ein kürzerer Poll-Intervall sinnvoll sein.

Mit diesen Informationen lässt sich die Diskrepanz zwischen Testumgebung (schnell) und Produktivsystem (verzögert) gezielter untersuchen.

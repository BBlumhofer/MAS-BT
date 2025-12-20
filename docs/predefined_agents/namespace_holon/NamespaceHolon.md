# Namespace Holon

The **NamespaceHolon** is the new parent holon that terminates all external MQTT communication for a namespace and spawns agent-specific sub-holons. Manufacturing and transport logic now run as sub-holons that only communicate with their parent; the parent mirrors (bridges) topics to the global namespace.

## Responsibilities

- Connect to the public MQTT namespace and expose the configured agent identity.
  - Configure the topic bridge via `ConfigureNamespaceTopicBridgeNode` so that external topics such as `/_{NS}/ManufacturingSequence/Request`, `/_{NS}/ManufacturingSequence/Response`, `/_{NS}/TransportPlan/*`, etc. are mirrored to the internal manufacturing or transport namespaces.
- Spawn and monitor sub-holons that are listed under `SubHolons` in the config (defaults: `ManufacturingDispatcher.json` and `TransportManager.json`).
- Wait until the sub-holons register back to the parent (`WaitForRegistration`) before it registers itself in the namespace.
- Keep sending heartbeats/registrations so downstream agents still see one dispatcher identity.

## Topic Bridge

The parent node registers a set of mapping rules inside `TopicBridgeService`:

- `/ExternalNamespace/ManufacturingSequence/Request` ↔ `/ExternalNamespace/{NamespaceHolon}/Manufacturing/ManufacturingSequence/Request`
- `/ExternalNamespace/ManufacturingSequence/Response` ↔ `/ExternalNamespace/{NamespaceHolon}/Manufacturing/ManufacturingSequence/Response`
- `/ExternalNamespace/ManufacturingSequence/Request` ↔ `/ExternalNamespace/{NamespaceHolon}/Manufacturing/ManufacturingSequence/Request`
- `/ExternalNamespace/ManufacturingSequence/Response` ↔ `/ExternalNamespace/{NamespaceHolon}/Manufacturing/ManufacturingSequence/Response`
- `/ExternalNamespace/TransportPlan/Request` ↔ `/ExternalNamespace/{NamespaceHolon}/Transport/TransportPlan/Request`
- `/ExternalNamespace/TransportPlan/Response` ↔ `/ExternalNamespace/{NamespaceHolon}/Transport/TransportPlan/Response`
- `/response/BookStep` ↔ manufacturing namespace
- Registration/Inventory updates are forwarded from the external namespace to manufacturing so the dispatcher sees module state, but they are **not** sent back (sub-holons only talk to the parent).
- ManufacturingSequence responses are also fanned out to the transport namespace so the `TransportManager` can track the latest storage location per product.

The bridge subscribes via the raw `IMessagingTransport.MessageReceived` event to keep payloads untouched and forwards them byte-for-byte. A short suppression window prevents the mirror from looping when the parent reflects messages back to their origin.

## Configuration

Configs live in `configs/specific_configs/NamespaceHolon`. The root config declares external MQTT connectivity plus sub-holons:

```json
{
  "Agent": {
    "AgentId": "NamespaceHolon_phuket",
    "InitializationTree": "Trees/NamespaceHolon.bt.xml"
  },
  "Namespace": "_PHUKET",
  "ExternalNamespace": "_PHUKET",
  "NamespaceHolon": {
    "ManufacturingSuffix": "Manufacturing",
    "TransportSuffix": "Transport",
    "ExpectedSubHolons": 2
  },
  "SubHolons": [
    "ManufacturingDispatcher.json",
    "TransportManager.json"
  ]
}
```

Each sub-holon config sets its namespace to `_{External}/{NamespaceHolon}/{Suffix}` and contains `NamespaceHolon.ParentAgentId` so the `RegisterAgent` nodes talk to the parent instead of the outside world.

## Running the holarchy

- Start the parent holon with `dotnet run -- NamespaceHolon/NamespaceHolon`.
- The parent will spawn the two sub-holons automatically (it uses `SpawnSubHolonsNode`, so the CLI switch `--spawn-terminal` still works).
- Planning/Execution/Product/Module agents keep using the public namespace (`/_PHUKET/...`) and talk to the NamespaceHolon; the sub-holons only see the internal topics.

This separation keeps the dispatcher logic responsive even when one flow is slow (e.g., transport) and makes it easier to evolve manufacturing vs. transport responsibilities independently.

# MAS-BT Progress & Status

**Last Updated**: 2025-12-15

---

## Current Status

### ✅ Completed Features

#### ProcessChain Workflow (2025-12-14)
- **Full ProcessChain generation pipeline**: Product Agent → Dispatching Agent → Planning Agents
- **Capability Matching**: Neo4j-based similarity matching with configurable thresholds
- **Offer Negotiation**: 
  - Inbox drain to capture early-arriving proposals
  - CfP reissue for modules that register after initial dispatch
  - Unified Offer topic: `/{Namespace}/DispatchingAgent/Offer`
  - Configurable timeout for offer collection
- **ProcessChain Building**: Aggregates all offers per RequiredCapability
- **Response Handling**: consent/refusal with detailed reason codes

#### Planning Agent (2025-12-09 onwards)
- **Initialization**: Loads CapabilityDescription from AAS, registers with Dispatching Agent
- **Offer Creation**:
  - `ParseCapabilityRequest`: Extracts RequiredCapability, RequirementId, ConversationId
  - `CapabilityMatchmaking`: Neo4j similarity scoring
  - `FeasibilityCheck`: Validates constraints (material, tools, storage)
  - `PlanCapabilityOffer`: Creates CapabilityOffer with Actions, Scheduling, Cost
  - `SendCapabilityOffer`: Publishes offer or refusal
- **Dispatch Loop**: Selects schedulable Actions from ProductionPlan, sends SkillRequests to Execution
- **SkillResponse Handling**: Applies ActionState updates to ProductionPlan

#### Execution Agent (2025-12-09)
- **Initialization**: Connects to MQTT/OPC UA, locks module, ensures coupling, starts StartupSkill
- **Precondition System**:
  - Standard checks: Coupled, Locked, StartupSkill running
  - Action-specific: InStorage (SlotContentType, SlotValue)
  - Precondition failures → requeue with exponential backoff
  - Configurable: `PreconditionBackoffStartMs`, `MaxPreconditionRetries`
- **SkillRequest Queue**:
  - FIFO queue with metadata (ConversationId, ActionId, InputParameters)
  - Queue snapshots published to `/{Namespace}/{ModuleId}/ActionQueue`
  - Backoff window (`NextRetryUtc`) prevents busy-looping
- **Skill Execution**: ExecuteSkill → Monitor → Reset → UpdateInventory → ActionState (DONE)
- **Error Handling**: Refusal when module busy, failure when skill fails

#### Messaging & Serialization (2025-12-09)
- **I4.0 Sharp Messaging**: Uses BaSyx FullSubmodelElementConverter + JsonStringEnumConverter
- **InteractionElements**: All messages include populated `value` fields
- **SkillRequest Parsing**: Auto-typing of parameters (string "true" → bool) to avoid OPC UA BadTypeMismatch
- **Log Messages**: SendLogMessageNode publishes structured logs via MQTT

#### Monitoring & Inventory (2025-12-09)
- **Inventory Updates**: 
  - Topic: `/{Namespace}/{ModuleId}/Inventory`
  - Payload: StorageUnits with InventorySummary (free/occupied)
  - Debounced publishing to avoid rapid-fire updates
- **Neighbor Information**: Published via `PublishNeighbors`
- **Queue Snapshots**: Published on every enqueue/requeue/dequeue

#### Module Holon Router (2025-12)
- Wrapper agent that combines Planning + Execution
- Registers with Dispatching Agent
- Routes offers/scheduling/booking between Dispatcher and sub-agents

---

## Known Issues & Limitations

### Build Warnings
- **NU1510**: System.Text.Json pruning warning (can be ignored)
- **CS0108**: ProcessParametersValidNode hides Equals (harmless)

### OPC UA
- KeepAlive can report transient `Bad` status → SDK reconnects automatically
- Some skill state values from server (e.g., 17) need clearer mapping/logging

### ProcessChain Negotiation (Fixed 2025-12-14)
- ~~Proposals arriving before callback registration~~ → **Fixed**: Inbox drain implemented
- ~~Modules registering after CfP dispatch~~ → **Fixed**: CfP reissue for late registrations
- ~~Race conditions between CfP and module registration~~ → **Fixed**: Unified topic and correlation

---

## Verification & Testing

### Build & Test
```bash
dotnet build MAS-BT.csproj -c Debug
dotnet test MAS-BT.csproj
```

### Example Runs
```bash
# Dispatching Agent
dotnet run -- configs/dispatching_agent.json

# Planning Agent (P102)
dotnet run -- configs/specific_configs/Module_configs/P102/P102_Planning_agent.json

# Execution Agent (P102)
dotnet run -- configs/specific_configs/Module_configs/P102/P102_Execution_agent.json

# Product Agent
dotnet run -- Trees/ProductAgent.bt.xml
```

### Observability

**ProcessChain Logs**:
- `CollectCapabilityOffer: drained X buffered messages from inbox for conversation {ConvId}`
- `CollectCapabilityOffer: re-issued Y CfP(s) to late-registered module {Module}`
- `CollectCapabilityOffer: recorded offer {OfferId} for capability {Capability}`
- `BuildProcessChainResponse: built process chain with {Count} requirements (success={Success})`

**Execution Agent Logs**:
- `ReadMqttSkillRequest: dequeued request for action {ActionId}`
- `CheckSkillPreconditions: preconditions not met, requeuing with backoff {BackoffMs}ms`
- `ExecuteSkill: starting skill {SkillName} with parameters {Params}`
- `SendSkillResponse: published ActionState {State} for conversation {ConvId}`

**Queue Monitoring**:
```bash
mosquitto_sub -t "/{Namespace}/{ModuleId}/ActionQueue" -v
```

**Inventory Monitoring**:
```bash
mosquitto_sub -t "/{Namespace}/{ModuleId}/Inventory" -v
```

---

## Next Steps

### High Priority
1. **Product Agent Offer Selection**: Implement logic to select best offers from ProcessChain
2. **ProductionPlan Creation**: Build ProductionPlan from selected offers and dispatch Actions
3. **ActionUpdate Messaging**: Publish ActionUpdate on every precondition retry with reason
4. **Integration Tests**: End-to-end tests for full ProcessChain → Execution workflow

### Medium Priority
1. **Manufacturing Sequence**: Implement RequestManufacturingSequence with transport planning
2. **Booking Mechanism**: Tentative → Confirmed booking workflow
3. **Recovery & Health Monitoring**: Continuous health checks, RecoverySequence subtrees
4. **Transport Agent**: Separate agent for transport coordination
5. **Telemetry & Metrics**: Queue length, oldest wait time, requeue count per action

### Low Priority / Nice-to-Have
1. **Standardize SlotContentType**: Define default behavior when missing (currently `Unknown`)
2. **Logging Improvements**: More structured ActionUpdate reasons, enum mappings in docs
3. **Code Cleanup**: Address build warnings (NU1510, CS0108), remove unused fields
4. **Multi-level Dispatching**: Hierarchical Dispatching Agent structure
5. **Advanced Scheduling Algorithms**: Minimize makespan, cost, load balancing

---

## References

### Core Documentation
- **Main Documentation**: `docs/MAS-BT_DOCUMENTATION.md`
- **Node Library**: `specs.json`

### Agent Documentation
- **Dispatching Agent**: `docs/predefined_agents/dispatching_agent/DispatchingAgent.md`
- **ProcessChain Pattern**: `docs/predefined_agents/dispatching_agent/ProcessChainPattern.md`
- **Module Holon**: `docs/predefined_agents/module_agent/ModuleHolon.md`
- **Execution Agent**: `docs/predefined_agents/execution_agent/MONITORING_AND_SKILL_NODES.md`
- **Execution TODOs**: `docs/predefined_agents/execution_agent/EXECUTION_AGENT_TODO.md`
- **Inventory MQTT**: `docs/predefined_agents/execution_agent/InventoryMQTT.md`

### Configuration
- **Configuration Nodes**: `docs/CONFIGURATION_NODES.md`
- **Startup & MQTT**: `docs/STARTUP_AND_MQTT.md`

### Capability Matching
- **Similarity Analysis**: `docs/SimilarityAnalysisAgent.md`
- **Neo4j Capability Matching**: `docs/CapabilityMatching_Neo4j.md`
- **Message Flow**: `docs/SimilarityAnalysisAgent_MessageFlow.md`

---

## Changelog

### 2025-12-14: ProcessChain Negotiation Robustness
- Implemented inbox drain in `CollectCapabilityOffer`
- Added CfP reissue for late-registering modules
- Unified Offer topic for CfP and proposals
- Removed semaphore-based throttling (replaced with ConversationId correlation)
- Improved WaitForMessage logging (debug for polling, warning for conversation-based)
- **Result**: ProcessChain successfully built with 3 requirements in test runs

### 2025-12-09: Preconditions & Queue Management
- Extended Preconditions data model (StoragePrecondition, PreconditionBase)
- Implemented SkillPreconditionChecker service
- ExecuteSkill evaluates preconditions before start
- Precondition failures → requeue with backoff (no immediate ERROR)
- Queue snapshots published on every state change
- MaxPreconditionRetries and PreconditionBackoffStartMs configurable

### 2025-12-09: Messaging & Serialization
- BaSyx FullSubmodelElementConverter + JsonStringEnumConverter
- InteractionElements with populated value fields
- SkillRequest auto-typing (string → bool/int/double)
- Log messages with structured payloads

### 2025-12-09: Inventory & Monitoring
- Inventory published to `/{Namespace}/{ModuleId}/Inventory`
- InventorySummary (free/occupied) included in StorageUnits
- Debounced publishing
- Lock-aware queue: requests wait when module unlocked

### Earlier: Lock-aware Execution Queue
- SkillRequests stay queued when module lock is missing
- ExecuteSkill waits until lock is reacquired
- No duplicate error responses during lock wait

### Earlier: Step Updates & Response Handling
- Step snapshots published to `/{Namespace}/{ModuleId}/StepUpdate`
- AwaitSkillResponse and ApplySkillResponse nodes
- Step synchronization rules (first Action EXECUTING → Step EXECUTING, all DONE → Step DONE)

---

**Status Summary**: ProcessChain workflow fully operational. Planning and Execution agents tested and stable. Precondition-based queue management prevents premature failures. Next focus: Product Agent offer selection and ProductionPlan creation.

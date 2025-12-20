# MAS-BT - Multi-Agent System with Behavior Trees

## Overview

**MAS-BT** is a holonic multi-agent system for flexible production systems, orchestrated using **Behavior Trees**. Each agent (Dispatching, Planning, Execution, Product, Module) is controlled by XML-defined behavior trees that manage decision-making, communication, and execution.

### Key Features

- **Behavior Tree-driven Agents**: All agent logic defined through XML behavior trees
- **Holonic Architecture**: Hierarchical agents with Planning and Execution sub-holons
- **Namespace Holon Gateway**: Parent holon (NamespaceHolon) bridges MQTT topics for manufacturing + transport sub-holons so they only communicate with their parent
- **ProcessChain Generation**: Automatic process chain creation from required capabilities
- **OPC UA Integration**: Real-time machine communication (skills, monitoring, inventory)
- **AAS-based Semantics**: Asset Administration Shell for process definitions and capability descriptions
- **MQTT Messaging**: Asynchronous inter-agent communication (I4.0 Sharp Messaging)
- **Precondition-based Execution**: Skill requests execute only when all preconditions are satisfied

### Technology Stack

- **.NET 10.0**
- **BehaviorTree.CPP** (Format 4)
- **OPC UA** (via Skill-Sharp-Client)
- **AAS** (via AAS-Sharp-Client)
- **MQTT** (via I4.0-Sharp-Messaging)
- **Neo4j** (Capability matching)

---

## Quick Start

### Prerequisites

- .NET 10.0 SDK
- MQTT Broker (Mosquitto)
- OPC UA Server
- AAS Repository
- Neo4j (optional)

### Build

```bash
dotnet build MAS-BT.csproj
```

### Run Agents

**Namespace Holon (Manufacturing Dispatcher + Transport Manager):**
```bash
dotnet run -- NamespaceHolon/NamespaceHolon
```

**Vollständiges System (NamespaceHolon + Module + SimilarityAgent + Produkt):**
```bash
dotnet run -- phuket_full_system
```

**Planning Agent (Module P102):**
```bash
dotnet run -- configs/specific_configs/Module_configs/P102/P102_Planning_agent.json
```

**Execution Agent (Module P102):**
```bash
dotnet run -- configs/specific_configs/Module_configs/P102/P102_Execution_agent.json
```

**Product Agent:**
```bash
dotnet run -- Trees/ProductAgent.bt.xml
```

> Tip: `dotnet run -- product_dispatch` still launches the product agent and dispatcher stack, but it now spawns the NamespaceHolon (which in turn launches its ManufacturingDispatcher and TransportManager sub-holons). Use `dotnet run -- phuket_full_system` to start NamespaceHolon, all configured module holons, the similarity agent, and the reference product agent with a single command.

---

## System Architecture

```
Product Agent
    ↓ (ProcessChain Request)
Namespace Holon (Manufacturing Dispatcher + Transport Manager)
    ↓ (CfP & Offers via Manufacturing Dispatcher)
Planning Agents (per Module)
    ↓ (SkillRequest)
Execution Agents (per Module)
    ↓ (OPC UA)
Physical Machines
```

### Agent Types

1. **Namespace Holon**: Single entry point for the namespace; bridges MQTT topics between the external namespace and its sub-holons, registers with other agents, and spawns the Manufacturing + Transport sub-holons.
2. **Manufacturing Dispatcher (sub-holon)**: Former DispatchingAgent tree; now runs inside the NamespaceHolon and handles sequential CfPs, capability matchmaking, and manufacturing sequence responses.
3. **Planning Agent**: Creates offers for capabilities, manages scheduling, dispatches skill requests
4. **Execution Agent**: Executes skills via OPC UA, checks preconditions, manages queue
5. **Product Agent**: Requests ProcessChain, manages ProductionPlan, monitors execution
6. **Module Holon**: Router wrapper combining Planning + Execution for one module

---

## ProcessChain Workflow (Implemented ✓)

1. **Product Agent** sends RequiredCapabilities to Dispatching Agent
2. **Manufacturing Dispatcher (NamespaceHolon)** sends CfPs (Call for Proposals) to Planning Agents
3. **Planning Agents** perform capability matching and create Offers
4. **Dispatching Agent** collects Offers and builds ProcessChain
5. **Product Agent** receives ProcessChain with all available Offers per Requirement
6. **Product Agent** uploads the ProcessChain to its AAS (or reuses an existing ProcessChain stored there) and sends a ManufacturingSequence request (set `Agent.RequestProcessChainOnly=true` to stop after the ProcessChain stage)

Key Features:
- Neo4j-based capability similarity matching
- Inbox drain for early-arriving proposals
- CfP reissue for late-registering modules
- Timeout handling with configurable wait times
- Automatic ManufacturingSequence request publishing once the ProcessChain is stored in the product shell (set `Agent.ForceProcessChainRequest=true` to always request a fresh ProcessChain; set `Agent.RequestProcessChainOnly=true` to skip the ManufacturingSequence phase)

---

## Planning & Execution (Implemented ✓)

### Planning Agent
- Receives CfPs from Dispatching Agent
- Performs feasibility checks (constraints, storage, transport)
- Creates CapabilityOffers with scheduling information and cost
- Dispatches Actions from ProductionPlan to Execution Agent

### Execution Agent
- Receives SkillRequests from Planning Agent
- Checks preconditions: Coupled, Locked, StartupSkill running, InStorage
- Executes skills via OPC UA
- Manages queue with backoff for unmet preconditions
- Publishes ActionState updates (consent, inform, failure)

### Precondition Retry Mechanism
- Requests with unmet preconditions are requeued (not rejected)
- Exponential backoff: `PreconditionBackoffStartMs` * 2^attempts
- Max retries: `MaxPreconditionRetries` (default: 10)
- Queue snapshots published to `/{Namespace}/{ModuleId}/ActionQueue`

---

## Configuration

All configs follow this structure:

```json
{
  "Agent": {
    "AgentId": "P102_Planning",
    "Role": "PlanningAgent",
    "ModuleId": "P102",
    "ModuleName": "P102",
    "RegistrationIntervalMs": 5000
  },
  "Namespace": "_PHUKET",
  "MQTT": {
    "Broker": "localhost",
    "Port": 1883,
    "ClientId": "{AgentId}_{Role}"
  },
  "OPCUA": {
    "Endpoint": "opc.tcp://localhost:4840"
  },
  "AAS": {
    "ShellRepositoryEndpoint": "http://localhost:8081/shells",
    "SubmodelRepositoryEndpoint": "http://localhost:8081/submodels"
  },
  "Execution": {
    "PreconditionBackoffStartMs": 5000,
    "MaxPreconditionRetries": 10
  }
}
```

Configs are located in `configs/` (generic) and `configs/specific_configs/Module_configs/<ModuleId>/` (module-specific).

---

## Documentation

📖 **Complete documentation**: `docs/MAS-BT_DOCUMENTATION.md`

### Agent Documentation
- Dispatching Agent: `docs/predefined_agents/dispatching_agent/DispatchingAgent.md`
- ProcessChain Pattern: `docs/predefined_agents/dispatching_agent/ProcessChainPattern.md`
- Module Holon: `docs/predefined_agents/module_agent/ModuleHolon.md`
- Execution Agent: `docs/predefined_agents/execution_agent/MONITORING_AND_SKILL_NODES.md`
- Inventory MQTT: `docs/predefined_agents/execution_agent/InventoryMQTT.md`

### Configuration & Development
- Configuration Nodes: `docs/CONFIGURATION_NODES.md`
- Startup & MQTT: `docs/STARTUP_AND_MQTT.md`
- Node Library Specification: `specs.json`
- Progress & Known Issues: `docs/Progress.md`

### Capability Matching
- Similarity Analysis: `docs/SimilarityAnalysisAgent.md`
- Neo4j Matching: `docs/CapabilityMatching_Neo4j.md`

---

## Project Structure

```
MAS-BT/
├── BehaviorTree/          # BT Core Engine
├── Nodes/                 # BT Node Implementations
│   ├── Dispatching/       # ProcessChain nodes
│   ├── Planning/          # Offer, Feasibility nodes
│   ├── SkillControl/      # ExecuteSkill, Monitor nodes
│   └── ...
├── Services/              # Shared services (AAS, MQTT, OPC UA)
├── Trees/                 # Behavior Tree XML definitions
├── configs/               # Configuration files
├── docs/                  # Documentation
└── tests/                 # Unit/Integration tests
```

---

## Current Status (2025-12-15)

✅ **Completed:**
- ProcessChain generation workflow (Dispatching ↔ Planning)
- Capability matching with Neo4j similarity
- Offer negotiation with inbox drain and CfP reissue
- Planning Agent: Offer creation, scheduling, dispatch
- Execution Agent: Precondition checking, queue management, skill execution
- MQTT messaging with I4.0 Sharp
- OPC UA integration for skills and monitoring
- Precondition retry with exponential backoff

🔄 **In Progress:**
- Product Agent: Offer selection and ProductionPlan creation
- Manufacturing Sequence with transport planning
- Booking mechanism (tentative → confirmed)

📋 **Roadmap:**
- Transport Agent integration
- Multi-level Dispatching hierarchy
- Advanced scheduling algorithms
- Deadlock prevention

---

## Testing

```bash
# Build and test
dotnet build MAS-BT.csproj
dotnet test MAS-BT.csproj

# Example test run
dotnet run -- Examples/ActionExecutionTest.bt.xml
```

For integration tests, start the sandbox environment:

```bash
cd environment/playground-v3
docker compose up -d
```

---

## Troubleshooting

### MQTT Connection Issues
- Check broker: `mosquitto -v`
- Test connection: `mosquitto_pub -t test -m "hello"`
- See: `docs/STARTUP_AND_MQTT.md`

### OPC UA Connection Issues
- Verify OPC UA server is running
- Check endpoint in config: `OPCUA.Endpoint`
- OPC UA SDK handles transient `Bad` status automatically

### Preconditions Not Met
- Check lock status, coupling, StartupSkill
- Review `UpdateInventory` logs for InStorage precondition
- Adjust `MaxPreconditionRetries` and `PreconditionBackoffStartMs`

### ProcessChain Negotiation Fails
- Verify Planning Agents are running
- Check module registration: `/{Namespace}/register`
- Monitor CfP topics with `mosquitto_sub`
- Review Capability matching Neo4j logs

---

## License

See repository root for license information.

---

**Last Updated**: 2025-12-15

**Status**: ProcessChain workflow fully implemented and tested. Planning and Execution agents operational. Precondition-based queue management implemented.

# MAS-BT Documentation Index

**Last Updated**: 2025-12-15

This document provides an overview of all documentation files in the MAS-BT project.

---

## 📖 Main Documentation

### [README.md](../README.md)
Quick start guide and project overview. Start here for installation, basic usage, and system architecture.

### [MAS-BT_DOCUMENTATION.md](MAS-BT_DOCUMENTATION.md) ⭐
**Complete system documentation** covering:
- Architecture and integration layers
- All agent types (Dispatching, Planning, Execution, Product, Module Holon)
- ProcessChain workflow (step-by-step)
- Planning and Execution workflows
- Configuration guide
- Development guidelines
- Troubleshooting

### [Progress.md](Progress.md)
Current implementation status, completed features, known issues, changelog, and roadmap.

---

## 🤖 Agent Documentation

### Dispatching Agent
- **[DispatchingAgent.md](predefined_agents/dispatching_agent/DispatchingAgent.md)**: Complete specification of the Dispatching Agent (ProcessChain generation, offer negotiation, module registry)
- **[ProcessChainPattern.md](predefined_agents/dispatching_agent/ProcessChainPattern.md)**: ProcessChain request/response pattern, data models, implementation guide

### Module Holon
- **[ModuleHolon.md](predefined_agents/module_agent/ModuleHolon.md)**: Router agent that wraps Planning + Execution and registers with Dispatching Agent

### Execution Agent
- **[MONITORING_AND_SKILL_NODES.md](predefined_agents/execution_agent/MONITORING_AND_SKILL_NODES.md)**: Execution Agent nodes (ExecuteSkill, CheckPreconditions, SendSkillResponse, UpdateInventory)
- **[EXECUTION_AGENT_TODO.md](predefined_agents/execution_agent/EXECUTION_AGENT_TODO.md)**: Execution Agent roadmap and open TODOs
- **[InventoryMQTT.md](predefined_agents/execution_agent/InventoryMQTT.md)**: Inventory MQTT topic schema, payload format, verification steps

---

## ⚙️ Configuration & Setup

### [CONFIGURATION_NODES.md](CONFIGURATION_NODES.md)
Documentation of all configuration-related BT nodes (ReadConfig, ConnectToMessagingBroker, InitializeAgentState, etc.)

### [STARTUP_AND_MQTT.md](STARTUP_AND_MQTT.md)
Detailed guide on:
- Starting agents with different config variants
- MQTT ClientId placeholder resolution
- Troubleshooting MQTT connection issues
- Diagnostics and logging

---

## 🧠 Capability Matching & Similarity Analysis

### [SimilarityAnalysisAgent.md](SimilarityAnalysisAgent.md)
Overview of the Similarity Analysis Agent for Neo4j-based capability matching.

### [SimilarityAnalysisAgent_MessageFlow.md](SimilarityAnalysisAgent_MessageFlow.md)
Message flow diagrams for similarity analysis requests and responses.

### [CapabilityMatching_Neo4j.md](CapabilityMatching_Neo4j.md)
Neo4j integration for capability matching: graph structure, queries, similarity scoring.

### [HowToSeeSimilarityResults.md](HowToSeeSimilarityResults.md)
Step-by-step guide to viewing and interpreting similarity analysis results.

### [SimilarityAnalysisResults.md](SimilarityAnalysisResults.md)
Example similarity analysis results and interpretation.

---

## 🛠️ Development

### [specs.json](../specs.json)
**Node Library Specification**: Complete JSON specification of all BT nodes, their parameters, and behavior.

### [Konzept.md](Konzept.md)
High-level conceptual documentation (German): System goals, holonic architecture, scheduling model.

### [CFP_Routing.md](CFP_Routing.md)
Call-for-Proposal routing between Dispatching Agent and Planning Agents.

---

## 📁 Configuration Files

Configuration files are located in:
- `configs/generic_configs/`: Template configurations
- `configs/specific_configs/Module_configs/<ModuleId>/`: Module-specific configurations

Example configs:
- `configs/dispatching_agent.json`
- `configs/planning_agent.json`
- `configs/Execution_agent.json`
- `configs/product_agent.json`
- `configs/specific_configs/Module_configs/P102/P102_Planning_agent.json`
- `configs/specific_configs/Module_configs/P102/P102_Execution_agent.json`

See `configs/specific_configs/Readme.md` for details on module-specific configurations.

---

## 🌳 Behavior Trees

Behavior Tree definitions are located in `Trees/`:
- `DispatchingAgent.bt.xml`: Dispatching Agent behavior
- `PlanningAgent.bt.xml`: Planning Agent behavior
- `ExecutionAgent.bt.xml`: Execution Agent behavior
- `ProductAgent.bt.xml`: Product Agent behavior
- `ModuleHolon.bt.xml`: Module Holon router behavior
- `SimilarityAnalysisAgent.bt.xml`: Similarity Analysis Agent behavior
- `Neo4jTestAgent.bt.xml`: Neo4j test agent

---

## 📝 Additional Notes

### Deprecated/Outdated Documentation
The following older documentation has been cleaned up or superseded:
- Old README.md (UTF-16 encoded, verbose) → replaced with clean UTF-8 version
- Intermediate Progress.md updates → consolidated into current status

### Documentation Guidelines
When adding new documentation:
1. Update this index
2. Add references in `MAS-BT_DOCUMENTATION.md` if relevant
3. Update `Progress.md` if feature status changes
4. Add node specifications to `specs.json`

---

## Quick Navigation

| What do you want to do? | Where to look |
|--------------------------|---------------|
| Get started with MAS-BT | [README.md](../README.md) |
| Understand the full system | [MAS-BT_DOCUMENTATION.md](MAS-BT_DOCUMENTATION.md) |
| Check current status | [Progress.md](Progress.md) |
| Learn about Dispatching Agent | [DispatchingAgent.md](predefined_agents/dispatching_agent/DispatchingAgent.md) |
| Learn about ProcessChain | [ProcessChainPattern.md](predefined_agents/dispatching_agent/ProcessChainPattern.md) |
| Learn about Execution Agent | [MONITORING_AND_SKILL_NODES.md](predefined_agents/execution_agent/MONITORING_AND_SKILL_NODES.md) |
| Configure agents | [CONFIGURATION_NODES.md](CONFIGURATION_NODES.md) & [STARTUP_AND_MQTT.md](STARTUP_AND_MQTT.md) |
| Understand capability matching | [CapabilityMatching_Neo4j.md](CapabilityMatching_Neo4j.md) |
| Find node specifications | [specs.json](../specs.json) |
| Troubleshoot issues | [MAS-BT_DOCUMENTATION.md](MAS-BT_DOCUMENTATION.md#troubleshooting) |

---

**For any questions or contributions, see the main README.**

# MAS-BT Documentation Index

This directory contains comprehensive documentation for the MAS-BT holonic control system and its integrated libraries.

## 📚 Core Documentation

### System Architecture
- [Main README](../README.md) - Overview of the MAS-BT holonic control architecture
- [Konzept.md](../Konzept.md) - Detailed architectural concepts (German)
- [specs.json](../specs.json) - Formal specification of all behavior tree nodes

### Node Libraries
- [CONFIGURATION_NODES.md](../CONFIGURATION_NODES.md) - Configuration and initialization nodes
- [MONITORING_AND_SKILL_NODES.md](../MONITORING_AND_SKILL_NODES.md) - Monitoring and skill control nodes
- [EXECUTION_AGENT_TODO.md](../EXECUTION_AGENT_TODO.md) - Execution agent development backlog

## 🔧 Integration Libraries

### AAS-Sharp-Client (Asset Administration Shell)
- **[AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md)** ⭐ **Comprehensive Documentation**
  - Complete API reference
  - Integration patterns with MAS-BT
  - Usage examples and best practices
  - Troubleshooting guide
  - Submodel specifications (Nameplate, Skills, CapabilityDescription, MachineSchedule)

### I4.0-Sharp-Messaging
- External: [I4.0-Sharp-Messaging README](../../I4.0-Sharp-Messaging/README.md)
- Industry 4.0 messaging protocol implementation
- MQTT-based communication layer

### Skill-Sharp-Client (OPC UA)
- External: [Skill-Sharp-Client README](../../Skill-Sharp-Client/README.md)
- OPC UA client for real-time machine control
- Skill execution and monitoring

## 📖 Documentation Overview

### Getting Started

1. **Understand the Architecture**
   - Read [Main README](../README.md) for system overview
   - Review [Konzept.md](../Konzept.md) for detailed concepts
   - Study the layer model: OPC UA (execution) + AAS (semantic) + BT (control) + MQTT (communication)

2. **Learn the Node Libraries**
   - Start with [CONFIGURATION_NODES.md](../CONFIGURATION_NODES.md) for initialization
   - Continue with [MONITORING_AND_SKILL_NODES.md](../MONITORING_AND_SKILL_NODES.md) for runtime behavior
   - Reference [specs.json](../specs.json) for complete node specifications

3. **Integration Libraries**
   - **AAS-Sharp-Client**: Read [AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md) for semantic data access
   - **I4.0-Sharp-Messaging**: For holonic communication patterns
   - **Skill-Sharp-Client**: For OPC UA machine control

### By Use Case

#### Setting Up a Resource Holon
1. Configuration nodes ([CONFIGURATION_NODES.md](../CONFIGURATION_NODES.md))
   - ConnectToMessagingBroker
   - ConnectToModule
   - ReadShell, ReadCapabilities, ReadSkills, ReadSchedule (use [AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md))
   - CoupleModule, EnsurePortsCoupled

2. Monitoring setup ([MONITORING_AND_SKILL_NODES.md](../MONITORING_AND_SKILL_NODES.md))
   - CheckReadyState
   - CheckErrorState
   - CheckLockedState

3. Example Trees
   - `Trees/Init_and_ExecuteSkill.bt.xml`
   - `Trees/Examples/ResourceHolonInit.bt.xml`

#### Loading AAS Data
- Full guide: [AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md)
- Quick reference: [CONFIGURATION_NODES.md](../CONFIGURATION_NODES.md) § "Mit AAS Sharp Client"

#### Implementing Skill Execution
- Node reference: [MONITORING_AND_SKILL_NODES.md](../MONITORING_AND_SKILL_NODES.md)
- Skills from AAS: [AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md) § "Skills Submodel"
- Execution patterns: Check `Trees/Init_and_ExecuteSkill.bt.xml`

#### Understanding Schedules and Planning
- Concepts: [Main README](../README.md) § "3. Scheduling Model"
- Schedule drift: [AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md) § "MachineSchedule Submodel"
- Implementation: [CONFIGURATION_NODES.md](../CONFIGURATION_NODES.md) § "ReadMachineScheduleNode"

## 🗂️ File Organization

```
MAS-BT/
├── README.md                          # Main architecture overview
├── Konzept.md                         # Detailed concepts (German)
├── specs.json                         # Formal node specifications
├── CONFIGURATION_NODES.md             # Configuration node docs
├── MONITORING_AND_SKILL_NODES.md      # Monitoring/skill node docs
├── EXECUTION_AGENT_TODO.md            # Development backlog
├── known_issues.md                    # Known issues tracker
│
├── docs/                              # 📁 THIS DIRECTORY
│   ├── README.md                      # This index file
│   ├── AAS_SHARP_CLIENT.md           # ⭐ Comprehensive AAS client documentation
│   ├── single_tree/                   # Single tree examples
│   └── subtrees/                      # Reusable subtree patterns
│
├── Nodes/                             # Node implementations
│   ├── Configuration/                 # Initialization nodes
│   ├── Monitoring/                    # State monitoring nodes
│   ├── SkillControl/                  # Skill execution nodes
│   ├── Messaging/                     # MQTT messaging nodes
│   ├── Locking/                       # Resource locking nodes
│   └── Recovery/                      # Error recovery nodes
│
├── BehaviorTree/                      # BT engine implementation
│   ├── Core/                          # BTNode, BTContext, NodeStatus
│   └── Serialization/                 # XML loading, NodeRegistry
│
├── Services/                          # Integration services
│   ├── SkillSharpClientService.cs     # OPC UA wrapper
│   ├── RemoteServerMqttNotifier.cs    # MQTT state notifications
│   └── MqttLogger.cs                  # MQTT logging
│
├── Trees/                             # Behavior tree definitions
│   ├── Init_and_ExecuteSkill.bt.xml  # Main example
│   └── Examples/                      # More example trees
│
└── Examples/                          # Standalone examples
    ├── ModuleInitializationTestRunner.cs
    └── ResourceHolonInitialization.cs
```

## 🎯 Quick Links

### API References
- [AAS-Sharp-Client API](./AAS_SHARP_CLIENT.md#api-reference) - Complete API documentation
- [BTNode Base Class](../CONFIGURATION_NODES.md#btnode-basisklasse) - Behavior tree node interface
- [BTContext](../CONFIGURATION_NODES.md#btcontext) - Shared state management

### Examples
- [AAS Loading Examples](./AAS_SHARP_CLIENT.md#usage-examples) - Code examples for AAS operations
- [BT Integration Examples](./AAS_SHARP_CLIENT.md#example-6-bt-context-integration) - Behavior tree integration
- [Resource Holon Init](../CONFIGURATION_NODES.md#beispiel-resource-holon-initialisierung) - Complete initialization sequence

### Specifications
- [AAS Submodels](./AAS_SHARP_CLIENT.md#submodel-specifications) - Nameplate, Skills, Capabilities, Schedule
- [Node Library](../specs.json) - All nodes with inputs/outputs
- [Monitoring Nodes](../MONITORING_AND_SKILL_NODES.md) - Complete monitoring node reference

### Troubleshooting
- [AAS Troubleshooting](./AAS_SHARP_CLIENT.md#troubleshooting) - Common AAS issues and solutions
- [Known Issues](../known_issues.md) - System-wide known issues
- [FAQ](./AAS_SHARP_CLIENT.md#faq) - Frequently asked questions

## 📝 Contributing to Documentation

When adding new documentation:

1. **API Documentation**: Update [AAS_SHARP_CLIENT.md](./AAS_SHARP_CLIENT.md) for AAS-related changes
2. **Node Documentation**: Update appropriate section in [CONFIGURATION_NODES.md](../CONFIGURATION_NODES.md) or [MONITORING_AND_SKILL_NODES.md](../MONITORING_AND_SKILL_NODES.md)
3. **Examples**: Add to relevant documentation file with working code
4. **Index**: Update this file with new documentation references

### Documentation Standards

- ✅ Use markdown formatting
- ✅ Include code examples
- ✅ Add links to related documentation
- ✅ Keep examples executable and tested
- ✅ Update index when adding new docs

## 🔄 Version History

- **v1.0** (2024-12-08): Initial comprehensive AAS-Sharp-Client documentation
- **v0.9** (2024-12-07): Configuration and Monitoring node documentation
- **v0.8**: Initial repository structure and specs.json

---

**Last Updated:** December 8, 2024  
**Maintainer:** MAS-BT Development Team

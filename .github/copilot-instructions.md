# MAS-BT Copilot Instructions

## Quick Facts
- MAS-BT is a holonic control stack where every Resource, Module, Product, and Transport Holon executes a Behavior Tree defined under `Trees/*.bt.xml`; `Program.cs` selects `Examples/ModuleInitializationTestRunner` by default, which ticks the tree every 100 ms (`ModuleInitTestRunner.cs`).
- OPC UA (MachineState & skill control), AAS (ProductionPlan, ExecutionPlan, CapabilityDescription), and MQTT/I4.0 Sharp messaging form the three integration pillars; Behavior Trees orchestrate the handoff between them.
- Shared state lives in `BTContext` and follows strict naming: `module_{ModuleName}_ready`, `skill_{SkillName}_state`, `CurrentAction`, `CapabilityDescription_{AgentId}`, etc. Nodes assume these keys exist, so upstream writers must set them before downstream consumers run.
- Configuration is provided via `config.json` (or one of the `configs/*.json` templates). `ReadConfigNode` copies settings into context under `config.*`, which is why most BT nodes read `context.Get<config...>` rather than loading JSON themselves.

## Architecture & Data Flow
- `Nodes/` groups the reusable node implementations by concern (Configuration, Monitoring, Locking, Messaging, SkillControl, Recovery, Constraints, Planning). Public nodes extend `BTNode` and operate on `BTContext`, so keep shared helper methods in `Nodes/Common` where possible.
- `BehaviorTree/Serialization/NodeRegistry.cs` is the only place that wires node names to classes for XML deserialization; new nodes must be registered here (node names default to the class name without the `Node` suffix).
- `Services/` hosts adapters such as `RemoteServerMqttNotifier`, `SkillSharpClientService`, `ExecutionAgentService` that bridge the BT logic to the external libraries (`Skill-Sharp-Client`, `I40Sharp.Messaging`, `AasSharpClient`). Reuse those services rather than instantiating messaging clients inside individual nodes.
- Planning and execution agents reuse the same structural helpers: `Trees/PlanningAgent.bt.xml` polls MQTT SkillRequests, selects the next `Action` from the plan, and sends SkillRequests to Execution via `SendSkillRequestNode`; `Trees/ExecutionAgent.bt.xml` enforces preconditions, manages locking, and publishes ActionState events (see `README.md`, `docs/MachineSchedule_API.md`).
- Node behaviors are documented in `specs.json` (canonical node library) plus `CONFIGURATION_NODES.md`, `MONITORING_AND_SKILL_NODES.md`, and `docs/*.md`. When you add or change a node, update the matching spec/documentation and add the node to `specs.json` if it is meant for reuse.

## Runtime & Developer Workflows
- Build and test with `dotnet test MAS-BT.csproj` (the project is marked as a test project, so the default run includes diagnostics). Run `dotnet build MAS-BT.csproj` only when you intentionally want to skip tests.
- Launch agents with `dotnet run -- <config-or-tree>` from the repository root: `dotnet run -- configs/Execution_agent.json` runs `Trees/ExecutionAgent.bt.xml`, `dotnet run -- Trees/PlanningAgent.bt.xml` runs the planning tree, and `dotnet run -- Trees/ProductAgentInitialization.bt.xml` spins up the product agent (see README front section for more `cp`/`ln` recipes).
- Manual scenarios require the sandbox stack in `environment/playground-v3` (OPC UA server + Mosquitto). Bring it up with `docker compose up -d` from that directory before running any agent trees; the trees expect MQTT topics and OPC UA endpoints to already exist.
- `configs/specific_configs/Module_configs/<ModuleId>/` contains per-module JSON templates (e.g., `P103_Planning_agent.json`) that mirror the shared `config.json` structure; copy or symlink the desired template to `config.json` before running the runner if you prefer not to pass the config argument explicitly.

## Node Implementation Patterns
- Always call `Initialize(context, logger)` before running a node. Composite and decorator nodes hook `OnAbort`/`OnReset`, so override those methods when a node needs to clean up sockets, subscriptions, or resource locks.
- MQTT skill request nodes populate `CurrentAction`, `ActionId`, `ConversationId`, and `InputParameters` (case-insensitive) in the context; downstream nodes read those values instead of reparsing the MQTT payload (`ReadMqttSkillRequestNode`, `AwaitSkillResponse`).
- Lock handling lives in `Nodes/Locking/*` (e.g., `LockResourceNode` with retries and `RemoteModule.LockAsync` which is intentionally fast). Do not sprinkle implicit retries elsewhere—`LockResourceNode` is the single gatekeeper, and unlocking happens through the matching release nodes.
- Recovery logic is centralized under `Nodes/Recovery/` and wired into `Trees/Init_and_ExecuteSkill.bt.xml` with the `RecoverySequence` monitor branch; follow that structure when adding new failure handling instead of scattering custom recovery logic.
- Preconditions with queues follow the design described in `README.md` (precondition backoff, queue snapshots, `MaxPreconditionRetries`, etc.). When a precondition fails, the request is requeued with `NextRetryUtc` instead of the tree throwing, so downstream metrics and queue snapshots stay consistent.

## Testing & Documentation
- Extend the tests under `tests/` or the Behavior Tree XML examples in `Trees/Examples/*.bt.xml` rather than writing independent scripts; the test project runs the same runners that `dotnet run` would.
- Use `Examples/ResourceHolonInitialization.cs` and `Examples/ModuleInitializationTestRunner.cs` to step through configuration and monitor the context state when debugging. Those entry points already hook into BT logging and cancellation semantics.
- Keep docs in sync when behavior changes: `README.md`, `Progress.md`, `docs/MachineSchedule_API.md`, `docs/InventoryMQTT.md`, `CONFIGURATION_NODES.md`, `MONITORING_AND_SKILL_NODES.md`, and `EXECUTION_AGENT_TODO.md` explain high-level goals and scheduling rules that the code must honor.

Let me know if any section above needs clarification or more detail so I can iterate on the instructions.

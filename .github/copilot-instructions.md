# MAS-BT Copilot Instructions

## MAS-BT Quick Facts
- MAS-BT is a C#/.NET 10 holonic control stack: every agent (resource, module, product, transport) runs Behavior Trees defined under `Trees/*.bt.xml` and executed via `ModuleInitializationTestRunner`.
- OPC UA (real-time machine control) + AAS (semantic process data) + MQTT/I4.0 Sharp messaging are the three integration pillars; Behavior Trees coordinate all three layers.
- `BTContext` is the single source of shared truth; nodes must both read and write agreed keys (e.g., `module_{Name}_ready`, `skill_{SkillName}_state`, `CurrentAction`).
- `specs.json` and the docs in `/docs/*.md` describe the canonical node library; keep them in sync with any new node behavior.

## Architecture & Data Flow
- `Nodes/**` contains implementation by concern (Configuration, Monitoring, Locking, Messaging, SkillControl, Recovery, Constraints). Every public node derives from `BTNode` and operates on `BTContext`.
- `BehaviorTree/Serialization/NodeRegistry.cs` is the authoritative registry: register new nodes here (names default to class name without `Node`). Missing registrations break XML loading.
- `Services/RemoteServerMqttNotifier.cs`, `SkillSharpClientService.cs`, and friends adapt external libraries (`Skill-Sharp-Client`, `I4.0-Sharp-Messaging`, `AAS-Sharp-Client`); reuse them instead of talking to those libraries directly inside nodes.
- `config.json` feeds runtime endpoints, credentials, and timing knobs; `ReadConfigNode` copies entries into context under `config.*` so other nodes avoid manual JSON parsing.

## Runtime & Workflows
- Build/test with `dotnet test MAS-BT.csproj` (the csproj is marked `IsTestProject=true`, so even example runners live inside one test project). Use `dotnet build` only when you do not want to execute tests.
- Run BT examples via `dotnet run --example module-init-test` (loads `Trees/Init_and_ExecuteSkill.bt.xml`) or `dotnet run --example resource-init`; without `--example` the module init runner is executed.
- `Examples/ModuleInitializationTestRunner.cs` drives a BehaviorTree.CPP-style tick loop (100 ms ticks, Ctrl+C cancels). Keep long-running nodes non-blocking and respect `NodeStatus.Running` semantics.
- End-to-end manual testing expects the playground stack in `environment/playground-v3` (OPC UA server + Mosquitto) to be up via `docker compose up -d` before running the trees.

## Node Implementation Patterns
- Always call `Initialize(context, logger)` before executing nodes; composite/decorator nodes propagate `OnAbort`/`OnReset`, so custom nodes should clean up connections in those overrides when needed.
- Context keys follow conventions: `skill_{SkillName}_state`, `module_{ModuleName}_locked`, and `CapabilityDescription_{AgentId}`. Reuse these names so downstream nodes function without edits.
- Messaging nodes operate on AAS `Action` objects: `ReadMqttSkillRequestNode` now populates `CurrentAction`, `ActionId`, `ConversationId`, and a case-insensitive `InputParameters` dictionary. Downstream nodes must read from these entries instead of reparsing MQTT payloads.
- Lock management lives in `LockResourceNode` (with retry/backoff) while `RemoteModule.LockAsync` is intentionally immediate—do not add hidden retries in the client layer.

## Recovery, Queueing & Messaging Rules
- Recovery is centralized in `Nodes/Recovery/*` and wired through `RecoverySequence` + monitor branches in `Trees/Init_and_ExecuteSkill.bt.xml`. When monitoring discovers lock or startup failures, trigger `RecoverySequence` rather than inventing node-local hacks.
- Action processing is evolving toward an execution queue (see `EXECUTION_AGENT_TODO.md`): SkillRequests enqueue, `consent/refuse` acknowledge capacity, and Preconditions failures must emit ActionUpdates (`preconditions not satisfied`) instead of failing the tree.
- MQTT logging/state updates should go through `RemoteServerMqttNotifier` or `SendStateMessageNode`; avoid ad-hoc MQTT publishers so telemetry stays consistent.

## Testing & Documentation
- Automated tests live in `tests/*.cs` (currently stubs) plus BT-based integration tests under `Trees/Examples/*.bt.xml`. Extend these rather than writing bespoke console apps.
- For manual debugging, prefer `Examples/ResourceHolonInitialization.cs` to step through Configuration nodes and inspect context snapshots.
- Authoritative specs live in `README.md`, `Konzept.md`, `CONFIGURATION_NODES.md`, `MONITORING_AND_SKILL_NODES.md`, and the backlog `EXECUTION_AGENT_TODO.md`; align new behavior with the priorities stated there before shipping.

Let me know if any section above is unclear or missing detail so I can refine it.

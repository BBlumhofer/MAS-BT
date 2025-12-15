# Manufacturing Sequence Refactor

This document tracks the plan and progress for restructuring the Manufacturing Sequence handling across planning and dispatching agents.

## Goals

1. Introduce a dedicated `ManufacturingSequence` submodel class that reflects the new structure (RequiredCapabilities → OfferedCapabilitySequences → CapabilitySequence).
2. Ensure transport capabilities (derived from pre/post conditions) are inserted in the correct sequence slots without duplication.
3. Keep the existing `ProcessChain` submodel unchanged for classic process-chain requests.
4. Extend serialization/deserialization paths (planning agents, dispatcher, transport handler) to work with the new model.
5. Add tests that cover unit, integration, and end-to-end scenarios for Manufacturing Sequence negotiation.

## Implementation Plan

1. **Model Layer**
   - Create new AAS models in `AAS-Sharp-Client` for `ManufacturingSequence`, `ManufacturingRequiredCapability`, and `OfferedCapabilitySequence`.
   - Add helpers for managing `CapabilitySequence`, pre/post condition ordering, and duplicate detection.

2. **Planning Agent Changes**
   - Update `RequestTransportNode` / `PlanCapabilityOfferNode` to build capability sequences instead of flat supplemental lists.
   - Insert transport capabilities before/after the root capability based on pre/post conditions.
   - Prevent duplicate transport entries by checking `InstanceIdentifier`.

3. **Dispatcher Changes**
   - Modify `CollectCapabilityOfferNode` to parse the nested sequence data.
   - Update `BuildProcessChainResponseNode` (and successor) to emit either `ProcessChain` or `ManufacturingSequence`.
   - Ensure transport offers propagate into manufacturing sequences.

4. **Testing**
   - Unit tests for the new model helpers and transport sequence ordering.
   - Integration tests covering planner ↔ dispatcher messaging for manufacturing requests.
   - End-to-end test scenario verifying the final Manufacturing Sequence submodel layout.

## Progress Log

| Date (UTC) | Author | Notes |
|------------|--------|-------|
| 2025-12-15 | Codex  | Initial plan documented; baseline goals and implementation steps captured. |
| 2025-12-15 | Codex  | Added ManufacturingSequence model classes (sequence, required capability, capability sequence) to AAS-Sharp-Client; ready to wire into planning/dispatch flows. |
| 2025-12-15 | Codex  | Refactored MAS-BT dispatcher to emit ManufacturingSequence submodels, wired SendProcessChainResponse to send generic submodel, and ensured transport capability sequences avoid duplicates. |
| 2025-12-15 | Codex  | Updated integration/capability matchmaking tests to understand subtype-aware callForProposal messages so the new ManufacturingSequence flow can be verified automatically. |
| 2025-12-15 | Codex  | Verified the entire suite (`dotnet test`) passes again; next focus is wiring the ManufacturingSequence model end-to-end through planning/dispatch nodes. |
| 2025-12-15 | Codex  | Context wiring improved: dispatcher and WaitForMessage now store `ManufacturingSequence.Result`, and SendProcessChainResponse reads from the manufacturing stash, enabling ProductAgent uploads without falling back to raw messages. |
| 2025-12-15 | Codex  | Product agent now embeds the full ProcessChain element inside every ManufacturingSequence request, eliminating the indirection via submodel references. |
| 2025-12-15 | Codex  | Dispatcher now parses the embedded ProcessChain metadata, preserves original requirement identifiers/capability references, and reuses those references when building responses to avoid redundant Neo4j lookups. |
| 2025-12-15 | Codex  | Planning agent now orders transport requests per pre/post conditions, annotates supplemental capabilities accordingly, and tracks the original capability metadata (instance IDs/references) for downstream ManufacturingSequence assembly. |
| 2025-12-15 | Codex  | Added unit coverage for the planning agent’s transport sequencing so pre/post transports remain ordered in the ManufacturingSequence output. |
| 2025-12-15 | Codex  | Added dispatcher-focused integration coverage that drives CfP → proposal flow under the ManufacturingSequence mode, ensuring the resulting submodel embeds pre/post transport capabilities in order. |
| 2025-12-15 | Codex  | Refined `CollectCapabilityOffer` parsing so transport capability sequences survive the proposal round-trip; ManufacturingSequence integration tests now cover the entire dispatcher ↔ planner negotiation successfully. |

## Current Status

- **Models**: `ManufacturingSequence`, `ManufacturingRequiredCapability`, and `ManufacturingOfferedCapabilitySequence` ship with helper APIs for managing nested capability sequences.
- **Planning Agent**: `RequestTransportNode` + `PlanCapabilityOfferNode` build ordered capability sequences (pre/main/post), enforce dedupe, and tag placement metadata for downstream consumers.
- **Dispatcher**: `CollectCapabilityOffer` reconstructs nested capability entries; `BuildProcessChainResponse` emits either traditional `ProcessChain` or the new `ManufacturingSequence` while preserving requested references.
- **Testing**: Unit coverage validates sequencing logic; `DispatcherBuildsManufacturingSequenceWithTransportSequences` provides an integration/e2e safeguard that exercises CfP → proposal → ManufacturingSequence upload.

## Next Steps

1. Convert legacy integration tests (`Registration*`, `ModuleMessaging*`, etc.) to async/await to remove `xUnit1031` warnings.
2. Expand ManufacturingSequence upload handling on the Product agent (e.g., AAS persistence) once downstream consumers are ready.
3. Evaluate whether transport responses require richer metadata (duration/cost) for more accurate scheduling in future iterations.

Further updates will be appended here as implementation proceeds.

# Monitoring & Skill Control Nodes Documentation

This document describes all Monitoring and Skill Control Nodes implemented in **Phase 1** and **Phase 2** of the Execution Agent development.

---

## 📊 Phase 1: Core Monitoring Nodes

These nodes enable real-time monitoring of module states, errors, locks and skill execution via OPC UA.

### 1. CheckReadyState

**Purpose:** Checks if a module is ready to accept new work.

**Parameters:**
- `ModuleName` (string) - Name of the module to check

**Returns:**
- `Success` - Module is ready (not locked)
- `Failure` - Module is not ready or error occurred

**Context Variables:**
- `module_{ModuleName}_ready` (bool) - Ready state

**Implementation Details:**
- Uses `RemoteModule.IsLockedByUs` as simplified ready check
- TODO: Use `RemoteModule.ReadyState` property when available

**Example Usage:**
```xml
<CheckReadyState ModuleName="ScrewingStation" />
```

**Typical Use Cases:**
- Pre-execution validation before skill requests
- Resource allocation checks
- Precondition evaluation in execution loops

---

### 2. CheckErrorState

**Purpose:** Detects errors in module or skills (unexpected Halted states).

**Parameters:**
- `ModuleName` (string) - Module to monitor
- `ExpectedError` (int?, optional) - Specific error code to check for

**Returns:**
- `Success` - No errors detected
- `Failure` - Errors found (unintended Halted skills)

**Context Variables:**
- `module_{ModuleName}_has_error` (bool) - Error state

**Implementation Details:**
- Iterates through all skills in module
- Detects `SkillStates.Halted` as error indicator
- Logs warnings for each halted skill
- TODO: Track whether Halt was explicitly requested

**Example Usage:**
```xml
<CheckErrorState ModuleName="ScrewingStation" />
```

**Typical Use Cases:**
- Error detection in monitoring loops
- Recovery trigger logic
- Health checks before critical operations

---

### 3. CheckLockedState

**Purpose:** Extended lock checking with flexible expectations.

**Parameters:**
- `ModuleName` (string) - Module to check
- `ExpectLocked` (bool) - True = expect locked, False = expect free

**Returns:**
- `Success` - Lock state matches expectation
- `Failure` - Lock state mismatch

**Context Variables:**
- `module_{ModuleName}_locked` (bool) - Current lock state

**Implementation Details:**
- Uses `RemoteModule.IsLockedByUs`
- Flexible checking: can validate both locked AND free states
- Logs warnings on mismatches

**Example Usage:**
```xml
<!-- Verify module is locked by us -->
<CheckLockedState ModuleName="ScrewingStation" ExpectLocked="true" />

<!-- Verify module is free -->
<CheckLockedState ModuleName="ScrewingStation" ExpectLocked="false" />
```

**Typical Use Cases:**
- Lock validation after `LockResource`
- Verification before critical operations
- Deadlock detection and avoidance

---

### 4. MonitoringSkill

**Purpose:** Reads skill state and monitoring variables for closed-loop control.

**Parameters:**
- `ModuleName` (string) - Module containing the skill
- `SkillName` (string) - Skill to monitor

**Returns:**
- `Success` - Monitoring data read successfully
- `Failure` - Skill not found or error occurred

**Context Variables:**
- `skill_{SkillName}_state` (string) - Current skill state (SkillStates enum)
- `skill_{SkillName}_monitoring` (Dictionary) - Monitoring variables

**Implementation Details:**
- Reads `RemoteSkill.CurrentState` property
- TODO: Read MonitoringData variables when API available
- Stores state in context for decision-making

**Example Usage:**
```xml
<MonitoringSkill ModuleName="ScrewingStation" SkillName="Screw" />
```

**Typical Use Cases:**
- Real-time skill progress monitoring
- Parameter validation during execution
- Performance tracking and optimization

---

## 🎮 Phase 2: Skill Control Nodes

These nodes enable advanced skill lifecycle management (pause, resume, abort, retry).

### 5. WaitForSkillState

**Purpose:** Waits until a skill reaches a specific state (polling-based).

**Parameters:**
- `ModuleName` (string) - Module containing the skill
- `SkillName` (string) - Skill to monitor
- `TargetState` (string) - Expected state (e.g., "Completed", "Running", "Halted")
- `TimeoutSeconds` (int, default=60) - Maximum wait time
- `PollIntervalMs` (int, default=500) - Polling interval

**Returns:**
- `Success` - Target state reached
- `Failure` - Timeout occurred

**Context Variables:**
- `skill_{SkillName}_state` (string) - Final state when node returns

**Implementation Details:**
- Polls `RemoteSkill.CurrentState` every `PollIntervalMs`
- Parses `TargetState` as `SkillStates` enum
- Logs debug messages during polling

**Example Usage:**
```xml
<WaitForSkillState 
    ModuleName="ScrewingStation" 
    SkillName="Screw" 
    TargetState="Completed" 
    TimeoutSeconds="30" />
```

**Typical Use Cases:**
- Synchronization between BT and skill execution
- Waiting for skill completion before next step
- State transition validation

---

### 6. AbortSkill

**Purpose:** Aborts a running skill and waits for Halted state.

**Parameters:**
- `ModuleName` (string) - Module containing the skill
- `SkillName` (string) - Skill to abort
- `TimeoutSeconds` (int, default=30) - Wait timeout
- `WaitForHalted` (bool, default=true) - Wait until Halted state reached

**Returns:**
- `Success` - Skill aborted successfully
- `Failure` - Abort failed or timeout

**Context Variables:**
- `skill_{SkillName}_abort_requested` (bool) - Marks explicit abort (prevents false error detection)
- `skill_{SkillName}_state` (string) - Final state

**Implementation Details:**
- Calls `skill.HaltAsync()` (via dynamic call)
- Marks abort as explicitly requested in context
- Waits for `SkillStates.Halted` if `WaitForHalted=true`
- Skips if already halted

**Example Usage:**
```xml
<AbortSkill 
    ModuleName="ScrewingStation" 
    SkillName="Screw" 
    WaitForHalted="true" />
```

**Typical Use Cases:**
- Emergency stop logic
- Error recovery
- Process cancellation

---

### 7. PauseSkill

**Purpose:** Pauses a running skill (transitions to Suspended state).

**Parameters:**
- `ModuleName` (string) - Module containing the skill
- `SkillName` (string) - Skill to pause
- `TimeoutSeconds` (int, default=30) - Wait timeout

**Returns:**
- `Success` - Skill suspended successfully
- `Failure` - Skill not running or timeout

**Context Variables:**
- `skill_{SkillName}_state` (string) - Final state ("Suspended")

**Implementation Details:**
- Only works if current state is `SkillStates.Running`
- Calls `skill.SuspendAsync()` (via dynamic call)
- Waits for `SkillStates.Suspended`

**Example Usage:**
```xml
<PauseSkill 
    ModuleName="ScrewingStation" 
    SkillName="Screw" />
```

**Typical Use Cases:**
- Multi-resource coordination (wait for neighbor)
- Interruption for priority tasks
- Synchronization with external events

---

### 8. ResumeSkill

**Purpose:** Resumes a paused skill (transitions from Suspended to Running).

**Parameters:**
- `ModuleName` (string) - Module containing the skill
- `SkillName` (string) - Skill to resume
- `TimeoutSeconds` (int, default=30) - Wait timeout

**Returns:**
- `Success` - Skill resumed successfully
- `Failure` - Skill not suspended or timeout

**Context Variables:**
- `skill_{SkillName}_state` (string) - Final state ("Running")

**Implementation Details:**
- Only works if current state is `SkillStates.Suspended`
- Calls `skill.UnsuspendAsync()` (via dynamic call)
- Waits for `SkillStates.Running`

**Example Usage:**
```xml
<ResumeSkill 
    ModuleName="ScrewingStation" 
    SkillName="Screw" />
```

**Typical Use Cases:**
- Resuming after neighbor becomes available
- Continuing after priority task completion
- Recovery from suspension

---

### 9. RetrySkill

**Purpose:** Retries a failed skill execution with exponential backoff.

**Parameters:**
- `ModuleName` (string) - Module containing the skill
- `SkillName` (string) - Skill to retry
- `Parameters` (string, optional) - Comma-separated parameters (e.g., "ProductId=ABC,Speed=100")
- `MaxRetries` (int, default=3) - Maximum retry attempts
- `BackoffMs` (int, default=1000) - Initial backoff delay
- `ExponentialBackoff` (bool, default=true) - Double backoff after each failure

**Returns:**
- `Success` - Skill executed successfully (on any attempt)
- `Failure` - All retry attempts failed

**Context Variables:**
- `skill_{SkillName}_retry_attempts` (int) - Number of attempts made

**Implementation Details:**
- Resets skill if in Halted state before retry
- Calls `skill.ExecuteAsync()` with parameters
- Implements exponential backoff (1s → 2s → 4s)
- Logs warnings for each failed attempt

**Example Usage:**
```xml
<RetrySkill 
    ModuleName="ScrewingStation" 
    SkillName="Screw" 
    Parameters="ProductId=HelloWorld"
    MaxRetries="5"
    ExponentialBackoff="true" />
```

**Typical Use Cases:**
- Transient failure recovery
- Network or communication error handling
- Resource temporarily unavailable scenarios

---

## 🔗 Integration with Existing Nodes

### Compatibility

All new nodes integrate seamlessly with existing nodes:

- **ExecuteSkill** - Can be wrapped with `WaitForSkillState` or `RetrySkill`
- **LockResource/UnlockResource** - Use with `CheckLockedState` for validation
- **ReadStorage** - Combine with `CheckReadyState` for preconditions
- **SendLogMessage** - Replaced by automatic `MqttLogger` (Phase 0)

### Context Usage

All nodes follow the consistent pattern:

```csharp
// Reading from context
var server = Context.Get<RemoteServer>("RemoteServer");

// Writing to context
Context.Set($"skill_{SkillName}_state", state.ToString());
Context.Set($"module_{ModuleName}_ready", isReady);
```

---

## 🧪 Testing

### Unit Testing (TODO)

Create tests in `/tests/Nodes/`:
- `MonitoringNodesTests.cs` - Tests for Phase 1 nodes
- `SkillControlNodesTests.cs` - Tests for Phase 2 nodes

### Integration Testing

See test trees in `/Trees/Examples/`:
- `SkillLifecycleTest.bt.xml` - Demonstrates Pause/Resume/Abort
- `ErrorRecoveryTest.bt.xml` - Uses CheckErrorState + RetrySkill

---

## 📈 Performance Considerations

### Polling Intervals

- `WaitForSkillState`: Default 500ms polling - adjust based on expected skill duration
- Shorter intervals = faster response, higher CPU usage
- Longer intervals = slower response, lower CPU usage

### Timeouts

- Default 30-60 seconds suitable for most skills
- Long-running skills (>5 minutes) should increase timeout
- Network-sensitive operations may need longer timeouts

### Context Size

- Each node stores minimal data in context (1-3 entries)
- Context is cleared between tree executions
- No memory leak concerns for long-running agents

---

## 🚀 Future Enhancements

### Planned Improvements

1. **Event-based WaitForSkillState**: Replace polling with OPC UA subscriptions
2. **MonitoringData API**: Read actual monitoring variables when available
3. **Abort Tracking**: Track explicit aborts vs. unexpected halts
4. **RetrySkill Strategies**: Configurable backoff strategies (linear, exponential, custom)

---

## 📊 Node Summary

| Node | Phase | Purpose | Returns | Timeout |
|------|-------|---------|---------|---------|
| CheckReadyState | 1 | Module ready check | bool | - |
| CheckErrorState | 1 | Error detection | bool | - |
| CheckLockedState | 1 | Lock validation | bool | - |
| MonitoringSkill | 1 | Skill monitoring | state+data | - |
| WaitForSkillState | 2 | Wait for state | bool | 60s |
| AbortSkill | 2 | Abort skill | bool | 30s |
| PauseSkill | 2 | Pause skill | bool | 30s |
| ResumeSkill | 2 | Resume skill | bool | 30s |
| RetrySkill | 2 | Retry with backoff | bool | per attempt |

**Total Nodes Implemented:** 9 (4 Phase 1 + 5 Phase 2)

---

## 📚 Related Documentation

- [EXECUTION_AGENT_TODO.md](EXECUTION_AGENT_TODO.md) - Overall project roadmap
- [CONFIGURATION_NODES.md](CONFIGURATION_NODES.md) - Configuration and setup nodes
- [README.md](README.md) - System architecture overview
- [specs.json](specs.json) - Complete node library specification

---

**Last Updated:** December 6, 2025  
**Status:** Phase 1+2 Complete ✅  
**Next Phase:** Messaging Integration (Phase 3)

# Topic-Subscription Pattern - Problemanalyse & Lösungsvorschlag

## Problem: Planning-Agents subscriben auf falsche Topics

**Aktuell beobachtet:**
```
Planning-Agent (P101) subscribed:
- /_PHUKET/Planning/OfferedCapability/Request  ← FALSCH! Das ist für ModuleHolon
- /_PHUKET/TransportPlan/Request               ← FALSCH! Das ist für ModuleHolon
- /_PHUKET/P101/ScheduleAction                 ← OK (intern)
- /_PHUKET/P101/BookingConfirmation            ← OK (intern)
```

**Root Cause:**
`SubscribeAgentTopicsNode` hat nur 2 Branches:
1. `if (role.Contains("Dispatching"))` → Dispatcher-Topics
2. `else` → **Alle anderen Rollen** → ModuleHolon-Topics

Planning-Agent fällt in Branch 2, obwohl er **nicht** auf namespace-level topics subscriben sollte!

---

## Ist-Zustand: Agent-Hierarchie & Topic-Pattern

```
Namespace (_PHUKET)
│
├─ ManufacturingDispatcher (Dispatching)
│  └─ Topics:
│     • /{ns}/ManufacturingSequence/Request (eingehend: Product → Dispatcher)
│     • /{ns}/Planning/OfferedCapability/Response (eingehend: Planning → Dispatcher)
│     • /{ns}/register, /{ns}/+/register (registrierungen)
│     • /{ns}/+/Inventory (aggregation)
│
├─ TransportManager
│  └─ Topics:
│     • /{ns}/TransportPlan/Request
│
└─ Module (P100, P101, P102)
   │
   ├─ ModuleHolon (P100)
   │  └─ Topics:
   │     • /{ns}/Planning/OfferedCapability/Request  ← empfängt CfP vom Dispatcher
   │     • /{ns}/TransportPlan/Request
   │     • /{ns}/{moduleId}/Inventory (broadcast eigener inventory)
   │     • /{ns}/{moduleId}/ScheduleAction (von Dispatcher)
   │     • /{ns}/{moduleId}/BookingConfirmation (von Dispatcher)
   │     • /{ns}/{moduleId}/TransportPlan (von Dispatcher)
   │     • /{ns}/{moduleId}/register (Sub-Holon registrierungen)
   │
   ├─ Planning Sub-Holon (P100_Planning)
   │  └─ Topics (INTERN - nur von ModuleHolon):
   │     • /{ns}/{moduleId}/PlanningAgent/OfferedCapability/Request  ← forwarded von ModuleHolon
   │     • /{ns}/{moduleId}/PlanningAgent/ScheduleAction
   │     • /{ns}/{moduleId}/PlanningAgent/TransportRequest
   │
   └─ Execution Sub-Holon (P100_Execution)
      └─ Topics (INTERN - nur von ModuleHolon):
         • /{ns}/{moduleId}/ExecutionAgent/SkillRequest
         • /{ns}/{moduleId}/ExecutionAgent/SkillResponse
```

---

## Soll-Zustand: Klare Role-basierte Topic-Zuordnung

### 1. **DispatchingAgent** (Role: "Dispatching")
```csharp
Topics:
- /{ns}/ManufacturingSequence/Request           // Product → Dispatcher
- /{ns}/ManufacturingSequence/Response          // Module → Dispatcher (proposals)
- /{ns}/Planning/OfferedCapability/Response     // Planning → Dispatcher (offers)
- /{ns}/BookStep/Request
- /{ns}/TransportPlan/Request
- /{ns}/register, /{ns}/+/register              // Registrierungen
- /{ns}/+/Inventory                             // Inventory aggregation
```

### 2. **ModuleHolon** (Role: "ModuleHolon")
```csharp
Topics:
- /{ns}/Planning/OfferedCapability/Request      // CfP vom Dispatcher (forwards to Planning)
- /{ns}/TransportPlan/Request                   // Transport-Anfragen vom Dispatcher
- /{ns}/{moduleId}/Inventory                    // PUBLISH: eigene Inventory-Updates
- /{ns}/{moduleId}/ScheduleAction               // SUBSCRIBE: Scheduling vom Dispatcher
- /{ns}/{moduleId}/BookingConfirmation          // SUBSCRIBE: Booking-Bestätigungen
- /{ns}/{moduleId}/TransportPlan                // SUBSCRIBE: Transport-Plan-Updates
- /{ns}/{moduleId}/register                     // SUBSCRIBE: Sub-Holon Registrierungen
- /{ns}/{moduleId}/Neighbors                    // SUBSCRIBE: Nachbar-Updates
```

### 3. **PlanningHolon** (Role: "PlanningHolon")
```csharp
Topics (NUR intern via ModuleHolon):
- /{ns}/{moduleId}/PlanningAgent/OfferedCapability/Request
- /{ns}/{moduleId}/PlanningAgent/ScheduleAction
- /{ns}/{moduleId}/PlanningAgent/TransportRequest
- /{ns}/TransportPlan/Response                  // Direkt vom TransportManager
```

### 4. **ExecutionHolon** (Role: "ExecutionHolon")
```csharp
Topics (NUR intern via ModuleHolon):
- /{ns}/{moduleId}/ExecutionAgent/SkillRequest
- /{ns}/{moduleId}/ExecutionAgent/SkillResponse
```

### 5. **TransportManager** (Role: "TransportManager")
```csharp
Topics:
- /{ns}/TransportPlan/Request                   // Anfragen von Planning-Agents
- /{ns}/TransportPlan/Response                  // PUBLISH: Antworten
```

### 6. **SimilarityAgent** (Role: "AIAgent" / "SimilarityAgent")
```csharp
Topics:
- /{ns}/Similarity/Request                      // Von Dispatcher
- /{ns}/Similarity/Response                     // PUBLISH
- /{ns}/Description/Request
- /{ns}/Description/Response
```

---

## Lösungsvorschlag: Refactoring SubscribeAgentTopicsNode

### Option A: Explizite Role-Branches (Empfohlen)

```csharp
public override async Task<NodeStatus> Execute()
{
    var role = ResolveRole();
    var ns = ResolveNamespace();
    var moduleId = ModuleContextHelper.ResolveModuleId(Context);
    
    HashSet<string> topics;
    
    // Explizite Role-Zuordnung (keine Fallbacks)
    if (IsDispatching(role))
    {
        topics = BuildDispatchingTopics(ns);
    }
    else if (IsModuleHolon(role))
    {
        topics = BuildModuleHolonTopics(ns, moduleId);
    }
    else if (IsPlanningHolon(role))
    {
        topics = BuildPlanningHolonTopics(ns, moduleId);
    }
    else if (IsExecutionHolon(role))
    {
        topics = BuildExecutionHolonTopics(ns, moduleId);
    }
    else if (IsTransportManager(role))
    {
        topics = BuildTransportManagerTopics(ns);
    }
    else if (IsSimilarityAgent(role))
    {
        topics = BuildSimilarityAgentTopics(ns);
    }
    else
    {
        Logger.LogWarning("SubscribeAgentTopics: Unknown role '{Role}', no topics subscribed", role);
        return NodeStatus.Success; // Nicht fatal
    }
    
    return await SubscribeToTopics(topics);
}

// Helper-Methoden für klare Topic-Sets
private HashSet<string> BuildPlanningHolonTopics(string ns, string moduleId)
{
    return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // INTERN: Forwarded von ModuleHolon
        $"/{ns}/{moduleId}/PlanningAgent/OfferedCapability/Request",
        $"/{ns}/{moduleId}/PlanningAgent/ScheduleAction",
        $"/{ns}/{moduleId}/PlanningAgent/TransportRequest",
        
        // Direkt vom TransportManager
        $"/{ns}/TransportPlan/Response"
    };
}

private bool IsPlanningHolon(string role)
{
    return role.Contains("Planning", StringComparison.OrdinalIgnoreCase)
        || role.Equals("PlanningHolon", StringComparison.OrdinalIgnoreCase);
}
```

**Vorteile:**
- ✅ Explizit: Jede Role hat definierte Topics
- ✅ Keine unerwarteten Fallbacks
- ✅ Leicht erweiterbar (neue Roles hinzufügen)
- ✅ Testbar (Mock Role → prüfe Topics)

**Nachteile:**
- Mehr Code (aber übersichtlicher)

---

### Option B: Dedizierte Subscription-Nodes (Langfristig besser)

Anstatt eines generischen `SubscribeAgentTopics`:

```xml
<!-- In PlanningAgent.bt.xml -->
<SubscribePlanningHolonTopics name="Subscribe" ModuleId="{config.Agent.ModuleId}" />

<!-- In ModuleHolon.bt.xml -->
<SubscribeModuleHolonTopics name="Subscribe" ModuleId="{config.Agent.ModuleId}" />

<!-- In ManufacturingDispatcher.bt.xml -->
<SubscribeDispatchingTopics name="Subscribe" Namespace="{config.Namespace}" />
```

**Implementierung:**
```csharp
// Neue Klassen:
public class SubscribePlanningHolonTopicsNode : BTNode { ... }
public class SubscribeModuleHolonTopicsNode : BTNode { ... }
public class SubscribeDispatchingTopicsNode : BTNode { ... }
```

**Vorteile:**
- ✅ Maximal explizit (Topic-Set im Node-Namen)
- ✅ Keine Role-Detection notwendig
- ✅ BT zeigt direkt, welche Topics subscribed werden
- ✅ Einfacher zu debuggen

**Nachteile:**
- Breaking Change (alle BTs anpassen)
- Mehr Node-Klassen

---

## Empfehlung: Schrittweise Migration

### Phase 1: Quick Fix (sofort)
```csharp
// In SubscribeAgentTopicsNode.cs
if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
{
    topics = BuildDispatchingTopics(ns);
}
else if (role.Contains("Planning", StringComparison.OrdinalIgnoreCase)
         && !role.Contains("ModuleHolon", StringComparison.OrdinalIgnoreCase))
{
    // Planning Sub-Holon: NUR interne Topics
    topics = BuildPlanningHolonTopics(ns, primaryModuleId);
}
else if (role.Contains("Execution", StringComparison.OrdinalIgnoreCase)
         && !role.Contains("ModuleHolon", StringComparison.OrdinalIgnoreCase))
{
    // Execution Sub-Holon: NUR interne Topics
    topics = BuildExecutionHolonTopics(ns, primaryModuleId);
}
else if (role.Contains("ModuleHolon", StringComparison.OrdinalIgnoreCase))
{
    // ModuleHolon: Namespace-level + module-spezifische Topics
    topics = BuildModuleHolonTopics(ns, primaryModuleId, moduleIdentifiers);
}
else
{
    // Fallback für unbekannte Roles (z.B. TransportManager, S1)
    Logger.LogWarning("SubscribeAgentTopics: Role '{Role}' uses default subscription pattern", role);
    topics = BuildDefaultTopics(ns, primaryModuleId);
}
```

### Phase 2: Refactoring (nächste Woche)
- Option A implementieren (explizite Helper-Methoden)
- Unit-Tests für jede Role

### Phase 3: Breaking Change (optional)
- Option B implementieren (dedizierte Nodes)
- BT-Migration mit Rückwärtskompatibilität

---

## Sofort-Fix Code

```csharp
// Nach Zeile 140 in SubscribeAgentTopicsNode.cs einfügen:

// Planning Sub-Holon: NICHT auf namespace-level topics subscriben!
if (role.Contains("Planning", StringComparison.OrdinalIgnoreCase)
    || role.Equals("PlanningHolon", StringComparison.OrdinalIgnoreCase))
{
    var planningTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    
    // Nur interne Topics (forwarded vom ModuleHolon)
    foreach (var moduleId in moduleIdentifiers)
    {
        planningTopics.Add($"/{ns}/{moduleId}/PlanningAgent/OfferedCapability/Request");
        planningTopics.Add($"/{ns}/{moduleId}/PlanningAgent/ScheduleAction");
        planningTopics.Add($"/{ns}/{moduleId}/PlanningAgent/TransportRequest");
    }
    
    // Direkte Antworten vom TransportManager
    planningTopics.Add($"/{ns}/TransportPlan/Response");
    
    var success = 0;
    foreach (var topic in planningTopics)
    {
        try
        {
            await client.SubscribeAsync(topic).ConfigureAwait(false);
            success++;
            Logger.LogInformation("SubscribeAgentTopics: subscribed {Topic}", topic);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SubscribeAgentTopics: failed to subscribe {Topic}", topic);
        }
    }
    
    return success > 0 ? NodeStatus.Success : NodeStatus.Failure;
}

// Execution Sub-Holon: analog
if (role.Contains("Execution", StringComparison.OrdinalIgnoreCase)
    || role.Equals("ExecutionHolon", StringComparison.OrdinalIgnoreCase))
{
    var executionTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    
    foreach (var moduleId in moduleIdentifiers)
    {
        executionTopics.Add($"/{ns}/{moduleId}/ExecutionAgent/SkillRequest");
    }
    
    var success = 0;
    foreach (var topic in executionTopics)
    {
        try
        {
            await client.SubscribeAsync(topic).ConfigureAwait(false);
            success++;
            Logger.LogInformation("SubscribeAgentTopics: subscribed {Topic}", topic);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SubscribeAgentTopics: failed to subscribe {Topic}", topic);
        }
    }
    
    return success > 0 ? NodeStatus.Success : NodeStatus.Failure;
}

// Danach: ModuleHolon-Branch (existierender Code)
```

---

## Testing

```bash
# Nach Fix: Logs prüfen
dotnet run -- phuket_full_system --spawn-terminal 2>&1 | grep "SubscribeAgentTopics"

# Erwartung:
# Planning-Agent (P100_Planning):
#   SubscribeAgentTopics: subscribed /_PHUKET/P100/PlanningAgent/OfferedCapability/Request
#   SubscribeAgentTopics: subscribed /_PHUKET/TransportPlan/Response
#
# ModuleHolon (P100):
#   SubscribeAgentTopics: subscribed /_PHUKET/Planning/OfferedCapability/Request
#   SubscribeAgentTopics: subscribed /_PHUKET/P100/ScheduleAction
#
# Dispatcher:
#   SubscribeAgentTopics: subscribed /_PHUKET/ManufacturingSequence/Request
#   SubscribeAgentTopics: subscribed /_PHUKET/+/register
```

---

**Soll ich den Sofort-Fix implementieren?**

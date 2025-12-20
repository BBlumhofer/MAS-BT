# Generisches Topic-Pattern mit Role-basiertem Broadcast (Option B)

## Kern-Prinzip: 3-Layer Topic-Hierarchie

```
/{namespace}/{targetRole}/broadcast/{MessageType}     → Broadcast an alle Agents einer Role
/{namespace}/{agentId}/{MessageType}                   → Direct Message an spezifischen Agent
/{namespace}/{parentId}/{childRole}/{MessageType}      → Parent→Child Forward
```

---

## Pattern-Anwendung auf aktuelle Flows

### 1. Dispatcher → ModuleHolons (CfP Broadcast)

**Topic:**
```
/{ns}/ModuleHolon/broadcast/OfferedCapability/Request
```

**Publisher:** `DispatchingAgent`

**Subscribers:** Alle `ModuleHolon` (P100, P101, P102, ...)

**Receiver-Detection:**
- ModuleHolon prüft `message.Frame.Receiver`:
  - `Receiver.Role == "ModuleHolon"` → Betrifft alle ModuleHolons
  - `Receiver.Id == "P100"` → Nur P100 antwortet
  - `Receiver.Id == null && Receiver.Role == "ModuleHolon"` → Broadcast an alle

**Beispiel-Message:**
```json
{
  "frame": {
    "sender": { "id": "DispatchingAgent_phuket", "role": "Dispatching" },
    "receiver": { "id": null, "role": "ModuleHolon" },
    "type": "CallForProposal/OfferedCapability",
    "conversationId": "conv-123"
  }
}
```

---

### 2. ModuleHolon → Planning (Internal Forward)

**Topic:**
```
/{ns}/{moduleId}/Planning/OfferedCapability/Request
```

**Publisher:** `ModuleHolon` (z.B. P100)

**Subscriber:** `Planning Sub-Holon` (z.B. P100_Planning)

**Receiver-Detection:**
- Planning prüft `message.Frame.Receiver.Role == "PlanningHolon"`

**Beispiel-Message:**
```json
{
  "frame": {
    "sender": { "id": "P100", "role": "ModuleHolon" },
    "receiver": { "id": "P100_Planning", "role": "PlanningHolon" },
    "type": "CallForProposal/OfferedCapability",
    "conversationId": "conv-123"
  }
}
```

---

### 3. Planning → Dispatcher (Response)

**Topic:**
```
/{ns}/Planning/OfferedCapability/Response
```

**Publisher:** `Planning Sub-Holon` (alle P10x_Planning)

**Subscriber:** `DispatchingAgent`

**Keine Änderung nötig!** (Response-Topics bleiben namespace-level)

---

### 4. Dispatcher → Modules (Direct Scheduling)

**Topic:**
```
/{ns}/{moduleId}/ScheduleAction
```

**Publisher:** `DispatchingAgent`

**Subscriber:** Spezifischer `ModuleHolon` (z.B. P100)

**Receiver-Detection:**
- ModuleHolon prüft `message.Frame.Receiver.Id == "P100"`

---

### 5. ModuleHolon → Execution (Skill Request)

**Topic:**
```
/{ns}/{moduleId}/Execution/SkillRequest
```

**Publisher:** `ModuleHolon`

**Subscriber:** `Execution Sub-Holon`

**Receiver-Detection:**
- Execution prüft `message.Frame.Receiver.Role == "ExecutionHolon"`

---

## Vollständige Topic-Matrix

| Flow | Topic Pattern | Publisher Role | Subscriber Role | Receiver Check |
|------|---------------|----------------|-----------------|----------------|
| Dispatcher CfP broadcast | `/{ns}/ModuleHolon/broadcast/OfferedCapability/Request` | Dispatching | ModuleHolon | `Receiver.Role == "ModuleHolon"` |
| Transport Request broadcast | `/{ns}/ModuleHolon/broadcast/TransportPlan/Request` | Dispatching | ModuleHolon | `Receiver.Role == "ModuleHolon"` |
| ModuleHolon forward to Planning | `/{ns}/{moduleId}/Planning/OfferedCapability/Request` | ModuleHolon | PlanningHolon | `Receiver.Role == "PlanningHolon"` |
| ModuleHolon to Execution | `/{ns}/{moduleId}/Execution/SkillRequest` | ModuleHolon | ExecutionHolon | `Receiver.Role == "ExecutionHolon"` |
| Planning Response | `/{ns}/Planning/OfferedCapability/Response` | PlanningHolon | Dispatching | `Receiver.Role == "Dispatching"` |
| Direct Scheduling | `/{ns}/{moduleId}/ScheduleAction` | Dispatching | ModuleHolon | `Receiver.Id == "{moduleId}"` |
| Booking Confirmation | `/{ns}/{moduleId}/BookingConfirmation` | Dispatching | ModuleHolon | `Receiver.Id == "{moduleId}"` |
| Inventory Updates | `/{ns}/{moduleId}/Inventory` | ModuleHolon | Dispatching | `Receiver.Role == "Dispatching"` |
| Registrations (wildcard) | `/{ns}/+/register` | Any | Dispatching | N/A (wildcard) |

---

## Subscription-Logic pro Agent-Role

### DispatchingAgent
```csharp
var topics = new HashSet<string>
{
    // Broadcasts von außen (Product)
    $"/{ns}/ManufacturingSequence/Request",
    $"/{ns}/BookStep/Request",
    
    // Responses von Planning
    $"/{ns}/Planning/OfferedCapability/Response",
    
    // Inventory aggregation (von allen Modules)
    $"/{ns}/+/Inventory",
    
    // Registrierungen
    $"/{ns}/register",
    $"/{ns}/+/register"
};
```

### ModuleHolon
```csharp
var topics = new HashSet<string>
{
    // Broadcasts vom Dispatcher (für alle ModuleHolons)
    $"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request",
    $"/{ns}/ModuleHolon/broadcast/TransportPlan/Request",
    
    // Direct Messages an dieses Modul
    $"/{ns}/{moduleId}/ScheduleAction",
    $"/{ns}/{moduleId}/BookingConfirmation",
    $"/{ns}/{moduleId}/TransportPlan",
    
    // Sub-Holon Registrierungen
    $"/{ns}/{moduleId}/register",
    
    // Nachbar-Updates
    $"/{ns}/{moduleId}/Neighbors"
};
```

### PlanningHolon
```csharp
var topics = new HashSet<string>
{
    // NUR interne Topics vom Parent ModuleHolon
    $"/{ns}/{parentModuleId}/Planning/OfferedCapability/Request",
    $"/{ns}/{parentModuleId}/Planning/ScheduleAction",
    $"/{ns}/{parentModuleId}/Planning/TransportRequest",
    
    // Direkte Antworten vom TransportManager
    $"/{ns}/TransportPlan/Response"
};
```

### ExecutionHolon
```csharp
var topics = new HashSet<string>
{
    // NUR interne Topics vom Parent ModuleHolon
    $"/{ns}/{parentModuleId}/Execution/SkillRequest"
};
```

### TransportManager
```csharp
var topics = new HashSet<string>
{
    // Transport-Anfragen von Planning-Agents
    $"/{ns}/TransportPlan/Request"
};
```

---

## Receiver-Detection Helper

```csharp
public static class MessageTargetingHelper
{
    /// <summary>
    /// Prüft, ob diese Message für den aktuellen Agent bestimmt ist.
    /// </summary>
    public static bool IsTargetedAtAgent(I40Message message, string agentId, string agentRole)
    {
        var receiver = message?.Frame?.Receiver;
        if (receiver == null)
        {
            // Keine Receiver-Info → Legacy-Mode, alle verarbeiten
            return true;
        }

        // Explizit an diese Agent-ID adressiert
        if (!string.IsNullOrWhiteSpace(receiver.Id) 
            && receiver.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An diese Role adressiert (Broadcast)
        if (!string.IsNullOrWhiteSpace(receiver.Role) 
            && receiver.Role.Equals(agentRole, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An "Broadcast" adressiert (alle)
        if (string.Equals(receiver.Id, "Broadcast", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Receiver gesetzt, aber nicht für uns
        return false;
    }

    /// <summary>
    /// Prüft, ob Message an Child-Role weitergeleitet werden soll.
    /// </summary>
    public static bool ShouldForwardToChild(I40Message message, string childRole)
    {
        var receiver = message?.Frame?.Receiver;
        if (receiver == null)
        {
            // Keine Receiver-Info → Legacy-Mode, nicht forwarden
            return false;
        }

        // Message ist für Child-Role bestimmt (z.B. "PlanningHolon")
        return !string.IsNullOrWhiteSpace(receiver.Role)
            && receiver.Role.Equals(childRole, StringComparison.OrdinalIgnoreCase);
    }
}
```

---

## Migration: Code-Änderungen

### 1. SubscribeAgentTopicsNode.cs

**Änderung:** Neue Branches für Role-spezifische Subscriptions

```csharp
public override async Task<NodeStatus> Execute()
{
    var client = Context.Get<MessagingClient>("MessagingClient");
    var ns = Context.Get<string>("config.Namespace") ?? "phuket";
    var role = ResolveRole();
    var agentId = Context.Get<string>("config.Agent.AgentId") ?? Context.AgentId;
    
    HashSet<string> topics;
    
    if (role.Contains("Dispatching", StringComparison.OrdinalIgnoreCase))
    {
        topics = BuildDispatchingTopics(ns);
    }
    else if (role.Contains("ModuleHolon", StringComparison.OrdinalIgnoreCase))
    {
        var moduleId = Context.Get<string>("config.Agent.ModuleId") ?? agentId;
        topics = BuildModuleHolonTopics(ns, moduleId);
    }
    else if (role.Contains("Planning", StringComparison.OrdinalIgnoreCase))
    {
        var parentModuleId = Context.Get<string>("config.Agent.ModuleId");
        topics = BuildPlanningHolonTopics(ns, parentModuleId);
    }
    else if (role.Contains("Execution", StringComparison.OrdinalIgnoreCase))
    {
        var parentModuleId = Context.Get<string>("config.Agent.ModuleId");
        topics = BuildExecutionHolonTopics(ns, parentModuleId);
    }
    else if (role.Contains("TransportManager", StringComparison.OrdinalIgnoreCase))
    {
        topics = BuildTransportManagerTopics(ns);
    }
    else
    {
        Logger.LogWarning("SubscribeAgentTopics: Unknown role '{Role}', using minimal defaults", role);
        topics = new HashSet<string> { $"/{ns}/register" };
    }
    
    return await SubscribeToTopics(topics);
}

private HashSet<string> BuildModuleHolonTopics(string ns, string moduleId)
{
    return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Role-basierte Broadcasts vom Dispatcher
        $"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request",
        $"/{ns}/ModuleHolon/broadcast/TransportPlan/Request",
        
        // Direct Messages
        $"/{ns}/{moduleId}/ScheduleAction",
        $"/{ns}/{moduleId}/BookingConfirmation",
        $"/{ns}/{moduleId}/TransportPlan",
        $"/{ns}/{moduleId}/register",
        $"/{ns}/{moduleId}/Neighbors"
    };
}

private HashSet<string> BuildPlanningHolonTopics(string ns, string parentModuleId)
{
    if (string.IsNullOrWhiteSpace(parentModuleId))
    {
        Logger.LogError("SubscribeAgentTopics: PlanningHolon requires config.Agent.ModuleId (parent module)");
        return new HashSet<string>();
    }
    
    return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Nur interne Topics vom Parent ModuleHolon
        $"/{ns}/{parentModuleId}/Planning/OfferedCapability/Request",
        $"/{ns}/{parentModuleId}/Planning/ScheduleAction",
        $"/{ns}/{parentModuleId}/Planning/TransportRequest",
        
        // Direkte Antworten vom TransportManager
        $"/{ns}/TransportPlan/Response"
    };
}

private HashSet<string> BuildExecutionHolonTopics(string ns, string parentModuleId)
{
    if (string.IsNullOrWhiteSpace(parentModuleId))
    {
        Logger.LogError("SubscribeAgentTopics: ExecutionHolon requires config.Agent.ModuleId (parent module)");
        return new HashSet<string>();
    }
    
    return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        $"/{ns}/{parentModuleId}/Execution/SkillRequest"
    };
}
```

---

### 2. ForwardCapabilityRequestsNode.cs (ModuleHolon)

**Änderung:** Subscribe auf neues Broadcast-Topic, Receiver-Check hinzufügen

```csharp
public override async Task<NodeStatus> Execute()
{
    var client = Context.Get<MessagingClient>("MessagingClient");
    EnsureListener(client);

    if (!_pendingMessages.TryDequeue(out var message))
    {
        return NodeStatus.Failure; // Kein CfP wartet
    }

    var ns = Context.Get<string>("config.Namespace") ?? "phuket";
    var moduleId = Context.Get<string>("config.Agent.ModuleId") ?? Context.AgentId;
    var agentRole = Context.AgentRole ?? "ModuleHolon";

    // Prüfen, ob Message für diesen Agent bestimmt ist
    if (!MessageTargetingHelper.IsTargetedAtAgent(message, moduleId, agentRole))
    {
        Logger.LogDebug("ForwardCapabilityRequests: Message not targeted at {ModuleId}, skipping", moduleId);
        return NodeStatus.Success; // Nicht verarbeiten, aber kein Fehler
    }

    var conv = message.Frame?.ConversationId ?? Guid.NewGuid().ToString();
    Context.Set("LastReceivedMessage", message);
    Context.Set("ForwardedConversationId", conv);

    // Forward intern an Planning Sub-Holon
    var targetTopic = $"/{ns}/{moduleId}/Planning/OfferedCapability/Request";
    
    // Receiver auf PlanningHolon ändern (für Child-Targeting)
    var forwardedMessage = CloneMessageWithNewReceiver(message, $"{moduleId}_Planning", "PlanningHolon");

    await client.PublishAsync(forwardedMessage, targetTopic);
    Logger.LogInformation(
        "ForwardCapabilityRequests: forwarded CfP from {Sender} to internal Planning at {Topic} (conv={Conv})",
        message.Frame?.Sender?.Id,
        targetTopic,
        conv
    );

    return NodeStatus.Success;
}

private void EnsureListener(MessagingClient client)
{
    if (_listenerRegistered) return;

    var ns = Context.Get<string>("config.Namespace") ?? "phuket";
    // Subscribe auf Role-basierten Broadcast
    var broadcastTopic = $"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request";

    client.OnMessage(msg =>
    {
        if (msg?.Frame?.Type != null 
            && msg.Frame.Type.Contains("CallForProposal", StringComparison.OrdinalIgnoreCase))
        {
            _pendingMessages.Enqueue(msg);
        }
    });

    Logger.LogInformation("ForwardCapabilityRequests: listening on {Topic}", broadcastTopic);
    _listenerRegistered = true;
}

private static I40Message CloneMessageWithNewReceiver(I40Message original, string receiverId, string receiverRole)
{
    // Shallow-Clone mit neuem Receiver
    var cloned = new I40Message
    {
        Frame = new I40MessageFrame
        {
            Sender = original.Frame?.Sender,
            Receiver = new I40MessageParticipant
            {
                Identification = new I40MessageIdentification { Id = receiverId },
                Role = new I40MessageRole { Name = receiverRole }
            },
            Type = original.Frame?.Type,
            ConversationId = original.Frame?.ConversationId,
            MessageId = original.Frame?.MessageId,
            SemanticProtocol = original.Frame?.SemanticProtocol
        },
        InteractionElements = original.InteractionElements
    };
    return cloned;
}
```

---

### 3. Dispatcher CfP Publishing

**Änderung:** Publish auf neues Broadcast-Topic mit Role-Receiver

**Beispiel (wo immer Dispatcher CfP sendet):**
```csharp
var topic = $"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request";

var message = new I40MessageBuilder()
    .From("DispatchingAgent_phuket", "Dispatching")
    .To("Broadcast", "ModuleHolon") // ← Receiver.Role = "ModuleHolon"
    .WithType($"{I40MessageTypes.CALL_FOR_PROPOSAL}/OfferedCapability")
    .WithConversationId(conversationId)
    .AddElements(requirementElements)
    .Build();

await client.PublishAsync(message, topic);
Logger.LogInformation("Dispatcher: broadcast CfP to ModuleHolons on {Topic}", topic);
```

**Betroffene Nodes:**
- `CollectCapabilityOffersNode.cs` (ProcessChain)
- `CollectManufacturingOffersNode.cs` (ManufacturingSequence)

---

### 4. Planning Response (keine Änderung nötig)

Planning-Agents publishen bereits auf:
```csharp
$"/{ns}/Planning/OfferedCapability/Response"
```

Dispatcher subscribed darauf → **bleibt unverändert**.

---

## Testing-Strategie

### 1. Unit-Tests für MessageTargetingHelper

```csharp
[Fact]
public void IsTargetedAtAgent_BroadcastRole_ReturnsTrue()
{
    var message = new I40MessageBuilder()
        .From("Dispatcher", "Dispatching")
        .To(null, "ModuleHolon") // Broadcast an Role
        .Build();
    
    var result = MessageTargetingHelper.IsTargetedAtAgent(message, "P100", "ModuleHolon");
    Assert.True(result);
}

[Fact]
public void IsTargetedAtAgent_SpecificId_OnlyTargetReturnsTrue()
{
    var message = new I40MessageBuilder()
        .From("Dispatcher", "Dispatching")
        .To("P100", "ModuleHolon") // Nur an P100
        .Build();
    
    var p100Result = MessageTargetingHelper.IsTargetedAtAgent(message, "P100", "ModuleHolon");
    var p101Result = MessageTargetingHelper.IsTargetedAtAgent(message, "P101", "ModuleHolon");
    
    Assert.True(p100Result);
    Assert.False(p101Result);
}
```

---

### 2. Integration-Test: End-to-End CfP Flow

```bash
# 1. Start Dispatcher
dotnet run -- dispatching_agent

# 2. Start ModuleHolon P100 mit Sub-Holons
dotnet run -- P100 --spawn-terminal

# 3. Sende CfP von Product
mosquitto_pub -h localhost -t "/_PHUKET/ManufacturingSequence/Request" -m '{...}'

# Erwartete Logs:
# [Dispatcher] Published CfP to /_PHUKET/ModuleHolon/broadcast/OfferedCapability/Request
# [P100] ForwardCapabilityRequests: forwarded CfP to /_PHUKET/P100/Planning/OfferedCapability/Request
# [P100_Planning] Received CfP on /_PHUKET/P100/Planning/OfferedCapability/Request
# [P100_Planning] Published offer to /_PHUKET/Planning/OfferedCapability/Response
# [Dispatcher] Received offer from P100_Planning
```

---

### 3. Negative Test: Planning subscribed nicht auf namespace-level

```bash
# Planning-Agent darf NICHT subscriben auf:
/_PHUKET/ModuleHolon/broadcast/OfferedCapability/Request

# Prüfung:
dotnet run -- P100_Planning_agent 2>&1 | grep "subscribed"

# Erwartung: NUR
# SubscribeAgentTopics: subscribed /_PHUKET/P100/Planning/OfferedCapability/Request
# SubscribeAgentTopics: subscribed /_PHUKET/TransportPlan/Response
```

---

## Migration-Plan

### Phase 1: Infra-Code (1-2h)
- [ ] `MessageTargetingHelper` implementieren
- [ ] `SubscribeAgentTopicsNode` refactoren (neue Helper-Methoden)
- [ ] Unit-Tests für Receiver-Detection

### Phase 2: ModuleHolon Forwarding (1h)
- [ ] `ForwardCapabilityRequestsNode` anpassen (neues Topic, Receiver-Check)
- [ ] `SubscribeModuleHolonTopicsNode` entfernen (deprecated)

### Phase 3: Dispatcher Publishing (1h)
- [ ] `CollectCapabilityOffersNode` Topic-Update
- [ ] `CollectManufacturingOffersNode` Topic-Update
- [ ] Receiver.Role = "ModuleHolon" setzen

### Phase 4: Testing & Rollout (2h)
- [ ] Integration-Tests ausführen
- [ ] Logs prüfen (keine Planning-Agents auf broadcast-topics)
- [ ] Live-System testen mit phuket_full_system

---

## Rollback-Strategie

Falls Probleme auftreten:

```csharp
// In SubscribeAgentTopicsNode: Legacy-Mode aktivieren
public bool UseLegacyTopics { get; set; } = false;

if (UseLegacyTopics)
{
    // Alte Topics (vor Migration)
    topics.Add($"/{ns}/Planning/OfferedCapability/Request");
}
else
{
    // Neue Topics (nach Migration)
    topics.Add($"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request");
}
```

**Config-Flag in BT:**
```xml
<SubscribeAgentTopics UseLegacyTopics="false" />
```

---

## Vorteile des neuen Patterns

✅ **Klare Hierarchie:** Topic zeigt explizit, welche Role adressiert wird  
✅ **Skalierbar:** Ein Broadcast-Topic für N ModuleHolons (kein N×Topic-Publishing)  
✅ **Generisch:** Funktioniert für beliebige Parent-Child-Beziehungen  
✅ **Receiver-Check:** Message-Frame entscheidet, ob verarbeitet/forwarded wird  
✅ **Kein Cross-Talk:** Planning-Agents sehen nur ihre Parent-Module-Topics  
✅ **Testbar:** Receiver-Logic isoliert in Helper-Klasse  

---

## Nächster Schritt

**Soll ich Phase 1 (MessageTargetingHelper + SubscribeAgentTopicsNode Refactoring) implementieren?**

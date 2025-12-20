# Generic Topic Pattern - Implementation Guide

## Überblick

Dieses Dokument beschreibt die Implementierung des generischen Topic-Patterns für role-basierte Broadcasts im MAS-BT System (Stand: Dezember 2025).

## Kern-Prinzip

Das neue Pattern verwendet eine 3-Layer Topic-Hierarchie:

```
/{namespace}/{targetRole}/broadcast/{MessageType}     → Broadcast an alle Agents einer Role
/{namespace}/{agentId}/{MessageType}                   → Direct Message an spezifischen Agent
/{namespace}/{parentId}/{childRole}/{MessageType}      → Parent→Child Forward
```

## Implementierte Komponenten

### 1. MessageTargetingHelper (`MAS-BT/Utilities/MessageTargetingHelper.cs`)

Zentrale Utility-Klasse für Receiver-Detection in I4.0 Messages.

#### Methoden

```csharp
// Prüft ob Message für diesen Agent bestimmt ist
bool IsTargetedAtAgent(I40Message message, string agentId, string agentRole)

// Prüft ob Message an Child-Role weitergeleitet werden soll
bool ShouldForwardToChild(I40Message message, string childRole)

// Erkennt role-based Broadcast-Messages
bool IsBroadcastMessage(I40Message message)

// Klont Message mit neuem Receiver (für Forwarding)
I40Message CloneWithNewReceiver(I40Message original, string receiverId, string receiverRole)
```

#### Verwendung

```csharp
using MAS_BT.Utilities;

// In ModuleHolon: Prüfen ob CfP für uns ist
if (!MessageTargetingHelper.IsTargetedAtAgent(message, moduleId, "ModuleHolon"))
{
    return; // Nicht für uns
}

// In ModuleHolon: Forwarding an Planning
var forwardedMessage = MessageTargetingHelper.CloneWithNewReceiver(
    message, 
    $"{moduleId}_Planning", 
    "PlanningHolon"
);
```

### 2. SubscribeAgentTopicsNode (Refactored)

Rolle-spezifische Subscription-Logic mit klaren Helper-Methoden.

#### Topic-Matrix nach Role

| Role | Subscribed Topics | Zweck |
|------|------------------|-------|
| **Dispatching** | `/{ns}/ManufacturingSequence/Request`<br>`/{ns}/Planning/OfferedCapability/Response`<br>`/{ns}/+/register`<br>`/{ns}/+/Inventory` | Externe Requests, Responses von Planning, Registrierungen, Inventory-Aggregation |
| **ModuleHolon** | `/{ns}/ModuleHolon/broadcast/OfferedCapability/Request`<br>`/{ns}/ModuleHolon/broadcast/TransportPlan/Request`<br>`/{ns}/{moduleId}/ScheduleAction`<br>`/{ns}/{moduleId}/BookingConfirmation`<br>`/{ns}/{moduleId}/register` | Role-based Broadcasts vom Dispatcher, Direct Messages, Sub-Holon Registrierungen |
| **PlanningHolon** | `/{ns}/{parentModuleId}/Planning/OfferedCapability/Request`<br>`/{ns}/{parentModuleId}/Planning/ScheduleAction`<br>`/{ns}/TransportPlan/Response` | Nur interne Topics vom Parent ModuleHolon, direkte Antworten vom TransportManager |
| **ExecutionHolon** | `/{ns}/{parentModuleId}/Execution/SkillRequest` | Nur interne Topics vom Parent ModuleHolon |
| **TransportManager** | `/{ns}/TransportPlan/Request` | Transport-Anfragen von Planning-Agents |

#### Code-Struktur

```csharp
public override async Task<NodeStatus> Execute()
{
    var role = ResolveRole();
    var ns = Context.Get<string>("config.Namespace");
    
    HashSet<string> topics;
    
    if (role.Contains("Dispatching"))
        topics = BuildDispatchingTopics(ns);
    else if (role.Contains("ModuleHolon"))
        topics = BuildModuleHolonTopics(ns, moduleId);
    else if (role.Contains("Planning"))
        topics = BuildPlanningHolonTopics(ns, parentModuleId);
    else if (role.Contains("Execution"))
        topics = BuildExecutionHolonTopics(ns, parentModuleId);
    // ...
    
    return await SubscribeTopics(topics);
}
```

### 3. ForwardCapabilityRequestsNode (Umgebaut)

ModuleHolons forwarden CfPs intern an Planning Sub-Holons.

#### Änderungen

**Vorher:**
- Subscribed auf `/{ns}/Planning/OfferedCapability/Request`
- Forward zu mehreren Modules basierend auf `ModuleIdentifiers`

**Nachher:**
- Listener auf alle CfP-Messages via `client.OnMessage()`
- Prüft mit `MessageTargetingHelper.IsTargetedAtAgent()` ob Message für diesen ModuleHolon ist
- Forward nur zu eigenem Planning: `/{ns}/{moduleId}/Planning/OfferedCapability/Request`

#### Code

```csharp
public override async Task<NodeStatus> Execute()
{
    if (!_pendingMessages.TryDequeue(out var message))
        return NodeStatus.Failure;
    
    // Receiver-Check
    if (!MessageTargetingHelper.IsTargetedAtAgent(message, moduleId, agentRole))
    {
        Logger.LogDebug("Message not targeted at {ModuleId}, skipping", moduleId);
        return NodeStatus.Success;
    }
    
    // Forward intern an Planning
    var targetTopic = $"/{ns}/{moduleId}/Planning/OfferedCapability/Request";
    var forwardedMessage = MessageTargetingHelper.CloneWithNewReceiver(
        message, 
        $"{moduleId}_Planning", 
        "PlanningHolon"
    );
    
    await client.PublishAsync(forwardedMessage, targetTopic);
    return NodeStatus.Success;
}
```

### 4. DispatchCapabilityRequestsNode (Topic geändert)

Dispatcher publiziert CfPs auf role-based Broadcast-Topic.

#### Änderung

**Vorher:**
```csharp
var topic = TopicHelper.BuildNamespaceTopic(Context, "Planning/OfferedCapability/Request");
// → /{ns}/Planning/OfferedCapability/Request
```

**Nachher:**
```csharp
var topic = $"/{ns}/ModuleHolon/broadcast/OfferedCapability/Request";
```

#### Message-Struktur

Die `CapabilityCallForProposalMessage` setzt bereits korrekt:
```csharp
var cfpMessage = new CapabilityCallForProposalMessage(
    senderId: Context.AgentId,
    senderRole: Context.AgentRole,
    receiverId: tgtModule,
    receiverRole: "ModuleHolon",  // ← Role-based targeting
    conversationId: ctx.ConversationId,
    // ...
);
```

## Message Flow

### End-to-End CfP Flow (neu)

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. Dispatcher publishes CfP                                           │
│    Topic: /_PHUKET/ModuleHolon/broadcast/OfferedCapability/Request   │
│    Receiver: { role: "ModuleHolon" }                                  │
└──────────────────────────────────────────────────────────────────────┘
                             ↓
    ┌────────────────────────┼────────────────────────┐
    ↓                        ↓                        ↓
┌─────────┐            ┌─────────┐            ┌─────────┐
│ P100    │            │ P101    │            │ P102    │
│ (Module │            │ (Module │            │ (Module │
│  Holon) │            │  Holon) │            │  Holon) │
└─────────┘            └─────────┘            └─────────┘
    │                        │                        │
    │ IsTargetedAtAgent?     │ IsTargetedAtAgent?     │ IsTargetedAtAgent?
    │ ✓ Yes (Role match)     │ ✓ Yes (Role match)     │ ✓ Yes (Role match)
    ↓                        ↓                        ↓
┌──────────────────────────────────────────────────────────────────────┐
│ 2. ModuleHolons forward intern                                        │
│    Topic: /_PHUKET/P100/Planning/OfferedCapability/Request           │
│    Receiver: { id: "P100_Planning", role: "PlanningHolon" }          │
└──────────────────────────────────────────────────────────────────────┘
                             ↓
    ┌────────────────────────┼────────────────────────┐
    ↓                        ↓                        ↓
┌─────────────┐      ┌─────────────┐      ┌─────────────┐
│ P100_       │      │ P101_       │      │ P102_       │
│ Planning    │      │ Planning    │      │ Planning    │
└─────────────┘      └─────────────┘      └─────────────┘
    │                        │                        │
    │ Plan & send offer      │ Plan & send offer      │ Plan & send offer
    ↓                        ↓                        ↓
┌──────────────────────────────────────────────────────────────────────┐
│ 3. Planning agents respond                                            │
│    Topic: /_PHUKET/Planning/OfferedCapability/Response               │
│    Receiver: { role: "Dispatching" }                                  │
└──────────────────────────────────────────────────────────────────────┘
                             ↓
                      ┌─────────────┐
                      │ Dispatcher  │
                      └─────────────┘
```

## Konfiguration

### Config-Struktur (unverändert)

Die bestehenden Config-Files benötigen **keine Änderung**:

**P100.json (ModuleHolon):**
```json
{
  "Agent": {
    "AgentId": "P100",
    "Role": "ModuleHolon",
    "InitializationTree": "Trees/ModuleHolon.bt.xml"
  },
  "SubHolons": ["P100_Execution_agent", "P100_Planning_agent"],
  "Namespace": "_PHUKET"
}
```

**P100_Planning_agent.json:**
```json
{
  "Agent": {
    "AgentId": "P100",
    "ModuleId": "P100",  // ← Wichtig: Parent-Module-ID!
    "Role": "PlanningHolon",
    "InitializationTree": "Trees/PlanningAgent.bt.xml"
  },
  "Namespace": "_PHUKET"
}
```

### Behavior Tree (unverändert)

**ModuleHolon.bt.xml:**
```xml
<root BTCPP_format="4">
  <BehaviorTree ID="ModuleHolonMain">
    <Sequence>
      <SubscribeAgentTopics Role="{config.Agent.Role}" />
      <ForwardCapabilityRequests />
      <!-- ... -->
    </Sequence>
  </BehaviorTree>
</root>
```

**PlanningAgent.bt.xml:**
```xml
<root BTCPP_format="4">
  <BehaviorTree ID="PlanningAgentMain">
    <Sequence>
      <SubscribeAgentTopics Role="{config.Agent.Role}" />
      <!-- Planning logic -->
    </Sequence>
  </BehaviorTree>
</root>
```

## Testing

### Erwartete Log-Ausgaben

```bash
# 1. ModuleHolon subscriptions
[P100] SubscribeAgentTopics: subscribed /_PHUKET/ModuleHolon/broadcast/OfferedCapability/Request
[P100] SubscribeAgentTopics: subscribed /_PHUKET/P100/ScheduleAction

# 2. Planning subscriptions (NUR interne Topics!)
[P100_Planning] SubscribeAgentTopics: subscribed /_PHUKET/P100/Planning/OfferedCapability/Request
[P100_Planning] SubscribeAgentTopics: subscribed /_PHUKET/TransportPlan/Response

# 3. CfP Flow
[Dispatcher] DispatchCapabilityRequests: published to /_PHUKET/ModuleHolon/broadcast/OfferedCapability/Request
[P100] ForwardCapabilityRequests: queued CfP conversation abc123 from DispatchingAgent
[P100] ForwardCapabilityRequests: forwarded CfP to /_PHUKET/P100/Planning/OfferedCapability/Request
[P100_Planning] Received CfP on /_PHUKET/P100/Planning/OfferedCapability/Request
```

### Test-Commands

```bash
# Build
dotnet build MAS-BT/MAS-BT.csproj

# Run full system
dotnet run --project MAS-BT/MAS-BT.csproj -- phuket_full_system --spawn-terminal

# Check subscriptions (in logs)
grep "SubscribeAgentTopics: subscribed" <log-file>

# Expected: NO Planning agents on /_PHUKET/ModuleHolon/broadcast/*
```

## Vorteile des neuen Patterns

### 1. Klare Hierarchie
Topics zeigen explizit welche Role adressiert wird:
- `/{ns}/ModuleHolon/broadcast/*` → für alle ModuleHolons
- `/{ns}/{moduleId}/Planning/*` → für Planning dieses Moduls

### 2. Kein Cross-Talk
Planning-Agents sehen nur noch ihre Parent-Module-Topics, keine namespace-level Broadcasts mehr.

### 3. Skalierbar
Ein Broadcast-Topic für N ModuleHolons (kein N×Topic-Publishing mehr).

### 4. Generisch
Pattern funktioniert für beliebige Parent-Child-Beziehungen:
```
Dispatcher → ModuleHolon → Planning/Execution
Dispatcher → TransportManager
ModuleHolon → ExecutionHolon (für Skills)
```

### 5. Receiver-basierte Logic
I4.0 Message Frame enthält explizite Receiver-Information:
```json
{
  "frame": {
    "sender": { "id": "DispatchingAgent", "role": "Dispatching" },
    "receiver": { "role": "ModuleHolon" },
    "type": "CallForProposal/OfferedCapability"
  }
}
```

## Migration von Alt zu Neu

### Betroffene Nodes

| Node | Änderung | Breaking? |
|------|----------|-----------|
| `SubscribeAgentTopicsNode` | Role-spezifische Branches | Nein (automatisch via Role) |
| `ForwardCapabilityRequestsNode` | Receiver-Check hinzugefügt | Nein |
| `DispatchCapabilityRequestsNode` | Topic geändert | **Ja** (aber transparent) |

### Rückwärtskompatibilität

**Ja, mit Einschränkung:**
- Alte Planning-Agents (die noch auf `/{ns}/Planning/OfferedCapability/Request` subscriben) empfangen **keine** CfPs mehr
- ModuleHolons müssen neu gestartet werden (für neue Subscription)
- Dispatcher muss neu gestartet werden (für neues Publish-Topic)

**Empfehlung:** Komplettes System-Restart nach Migration.

## Troubleshooting

### Planning-Agent empfängt keine CfPs

**Symptom:**
```
[P100_Planning] No capability requests received
```

**Check:**
```bash
# 1. Prüfe Planning Subscriptions
grep "P100_Planning.*subscribed" <log>
# Erwartung: /_PHUKET/P100/Planning/OfferedCapability/Request

# 2. Prüfe ModuleHolon Forward
grep "P100.*ForwardCapabilityRequests" <log>
# Erwartung: "forwarded CfP to /_PHUKET/P100/Planning/..."

# 3. Prüfe Dispatcher Publish
grep "DispatchCapabilityRequests.*published" <log>
# Erwartung: "published to /_PHUKET/ModuleHolon/broadcast/..."
```

**Lösung:**
- Config prüfen: `"ModuleId": "P100"` in Planning-Agent Config vorhanden?
- ModuleHolon neu starten (für neue Subscription)

### ModuleHolon empfängt keine Broadcasts

**Symptom:**
```
[P100] ForwardCapabilityRequests: No CfPs queued
```

**Check:**
```bash
# Prüfe ModuleHolon Subscriptions
grep "P100.*subscribed.*ModuleHolon/broadcast" <log>
# Erwartung: /_PHUKET/ModuleHolon/broadcast/OfferedCapability/Request
```

**Lösung:**
- Role in Config korrekt? `"Role": "ModuleHolon"`
- MQTT Broker läuft? `mosquitto -v`

### Receiver-Detection schlägt fehl

**Symptom:**
```
[P100] ForwardCapabilityRequests: Message not targeted at P100, skipping
```

**Check:**
```bash
# Debug-Logging in MessageTargetingHelper aktivieren
# Prüfe Receiver in Message:
{
  "frame": {
    "receiver": { 
      "id": null,  // ← Sollte null sein für broadcast
      "role": "ModuleHolon"  // ← Sollte "ModuleHolon" sein
    }
  }
}
```

**Lösung:**
- Dispatcher-Side: `CapabilityCallForProposalMessage` setzt `receiverRole: "ModuleHolon"`
- Message-Frame prüfen mit MQTT-Monitor

## Verwandte Dokumentation

- [Topic_Subscription_Pattern_Analysis.md](Topic_Subscription_Pattern_Analysis.md) - Problemanalyse
- [Generic_Topic_Pattern_Design.md](Generic_Topic_Pattern_Design.md) - Design-Konzept
- [Registration_System_Redesign_Concept.md](Registration_System_Redesign_Concept.md) - Registration-System

## Change Log

| Datum | Version | Änderung |
|-------|---------|----------|
| 2025-12-20 | 1.0 | Initial Implementation des Generic Topic Patterns |


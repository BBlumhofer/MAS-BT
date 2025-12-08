# I4.0 Sharp Messaging Client - Umfassende Dokumentation

## Inhaltsverzeichnis
1. [Übersicht](#übersicht)
2. [Architektur und Konzepte](#architektur-und-konzepte)
3. [Kernkomponenten](#kernkomponenten)
4. [Nachrichtenstruktur](#nachrichtenstruktur)
5. [Verbindung und Konfiguration](#verbindung-und-konfiguration)
6. [Nachrichten Senden und Empfangen](#nachrichten-senden-und-empfangen)
7. [Integration in MAS-BT](#integration-in-mas-bt)
8. [Nachrichtentypen](#nachrichtentypen)
9. [AAS-basierte Nachrichtenformate](#aas-basierte-nachrichtenformate)
10. [Best Practices](#best-practices)
11. [Troubleshooting](#troubleshooting)
12. [Beispiele](#beispiele)

---

## Übersicht

Der **I4.0 Sharp Messaging Client** ist eine C#/.NET-Bibliothek für die standardisierte Kommunikation in Industrie 4.0-Systemen. Sie implementiert den **VDI/VDE 2193-2** Standard für die Nachrichtenkommunikation in Multi-Agenten-Systemen und nutzt **MQTT** als Transport-Protokoll.

### Hauptmerkmale

- ✅ **Standardisierte I4.0-Nachrichten**: Implementierung des VDI/VDE 2193-2 Standards
- ✅ **MQTT-Transport**: Zuverlässige, ereignisbasierte Kommunikation
- ✅ **AAS-Integration**: Direkte Integration mit Asset Administration Shell (AAS)
- ✅ **Type-Safety**: Stark typisierte Nachrichtenelemente
- ✅ **Publisher/Subscriber Pattern**: Flexible Nachrichtenverteilung
- ✅ **Topic-basiertes Routing**: Hierarchische Nachrichtenorganisation

### Einsatz im MAS-BT System

In MAS-BT wird I4.0 Sharp Messaging für folgende Kommunikationsaufgaben verwendet:

- **Inter-Holon-Kommunikation**: Nachrichten zwischen Resource-, Product-, Module- und Transport-Holons
- **Skill-Anfragen und -Antworten**: Planning Agent → Execution Agent Kommunikation
- **Zustandsübermittlung**: Maschinenzustände, Locks, Bereitschaft
- **Logging und Monitoring**: Verteiltes Logging über MQTT
- **Action Updates**: Status-Updates während der Skill-Ausführung
- **Event-Benachrichtigungen**: Alarmierung bei kritischen Ereignissen

---

## Architektur und Konzepte

### Schichtenmodell

```
┌─────────────────────────────────────┐
│   Behavior Tree Nodes (MAS-BT)     │ ← Anwendungslogik
├─────────────────────────────────────┤
│   Services (MqttLogger, Notifier)   │ ← Service-Layer
├─────────────────────────────────────┤
│   I40Sharp.Messaging                │ ← Messaging-Framework
│   - MessagingClient                 │
│   - I40MessageBuilder               │
│   - Message Models                  │
├─────────────────────────────────────┤
│   I40Sharp.Messaging.Transport      │ ← Transport-Layer
│   - MqttTransport                   │
├─────────────────────────────────────┤
│   MQTT Broker (Mosquitto)           │ ← Infrastruktur
└─────────────────────────────────────┘
```

### Kommunikationsparadigmen

1. **Request-Reply**: Anfrage → Antwort (z.B. SkillRequest → SkillResponse)
2. **Publish-Subscribe**: Broadcasting (z.B. State Messages, Logs)
3. **Point-to-Point**: Direkte Agent-zu-Agent Kommunikation

### Nachrichtenfluss

```
Sender Agent                         MQTT Broker                         Receiver Agent
     │                                    │                                    │
     │ 1. Build I40Message                │                                    │
     │    (I40MessageBuilder)             │                                    │
     │                                    │                                    │
     │ 2. PublishAsync(msg, topic) ───────>                                    │
     │                                    │                                    │
     │                                    │ 3. Route by Topic                  │
     │                                    │                                    │
     │                                    │ 4. Deliver ─────────────────────────>
     │                                    │                                    │
     │                                    │                      5. OnMessage Callback
     │                                    │                         │
     │                                    │                         v
     │                                    │              6. Process Message
     │                                    │                 (Parse & Handle)
```

---

## Kernkomponenten

### 1. MessagingClient

Die zentrale Klasse für alle Messaging-Operationen.

**Initialisierung:**
```csharp
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;

// Transport erstellen
var transport = new MqttTransport(
    brokerHost: "localhost",
    brokerPort: 1883,
    clientId: "ResourceHolon_RH1"
);

// MessagingClient erstellen
var client = new MessagingClient(
    transport: transport,
    defaultTopic: "factory/agents/messages"
);

// Event-Handler registrieren
client.Connected += (s, e) => Console.WriteLine("Connected!");
client.Disconnected += (s, e) => Console.WriteLine("Disconnected!");

// Verbinden
await client.ConnectAsync();
```

**Wichtige Eigenschaften:**
- `IsConnected` (bool): Status der Verbindung
- `DefaultTopic` (string): Standard-Topic für Nachrichten

**Wichtige Methoden:**
- `ConnectAsync()`: Verbindung zum Broker herstellen
- `DisconnectAsync()`: Verbindung trennen
- `PublishAsync(message, topic)`: Nachricht senden
- `SubscribeAsync(topic)`: Topic abonnieren
- `UnsubscribeAsync(topic)`: Abonnement beenden
- `OnMessage(callback)`: Globaler Callback für alle Nachrichten

### 2. I40MessageBuilder

Fluent API zum Erstellen von I4.0-konformen Nachrichten.

**Grundstruktur:**
```csharp
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;

var message = new I40MessageBuilder()
    .From(senderId: "ResourceHolon_RH1", role: "ResourceHolon")
    .To(receiverId: "PlanningAgent_PA1", role: "PlanningAgent")
    .WithType(I40MessageTypes.INFORM)
    .WithConversationId(Guid.NewGuid().ToString())
    .WithMessageId(Guid.NewGuid().ToString())
    .ReplyingTo(originalMessageId)  // Optional
    .AddElement(messageElement)
    .Build();
```

**Builder-Methoden:**
- `From(id, role)`: Absender definieren
- `To(id, role)`: Empfänger definieren
- `WithType(type)`: Nachrichtentyp setzen (inform, consent, refusal, failure, etc.)
- `WithConversationId(id)`: Konversations-ID (für Request-Reply-Sequenzen)
- `WithMessageId(id)`: Eindeutige Nachrichten-ID
- `ReplyingTo(messageId)`: Referenz auf ursprüngliche Nachricht
- `AddElement(element)`: Payload hinzufügen (SubmodelElement)
- `Build()`: Nachricht finalisieren

### 3. I40Message

Die vollständige Nachrichtenstruktur.

**Struktur:**
```csharp
public class I40Message
{
    public I40Frame Frame { get; set; }
    public List<SubmodelElement> InteractionElements { get; set; }
}

public class I40Frame
{
    public string Type { get; set; }                    // inform, consent, etc.
    public string MessageId { get; set; }               // Eindeutige ID
    public string ConversationId { get; set; }          // Konversations-ID
    public string? ReplyTo { get; set; }                // Optional: Reply-Referenz
    public I40Participant Sender { get; set; }          // Absender
    public I40Participant Receiver { get; set; }        // Empfänger
    public DateTime Timestamp { get; set; }             // Zeitstempel
}

public class I40Participant
{
    public I40Identification Identification { get; set; }
    public string Role { get; set; }
}
```

### 4. MqttTransport

Low-Level MQTT-Transport-Implementierung.

**Konfiguration:**
```csharp
var transport = new MqttTransport(
    brokerHost: "mqtt.factory.local",
    brokerPort: 1883,
    clientId: "unique-client-id",
    username: "mqtt_user",      // Optional
    password: "mqtt_pass"       // Optional
);

// Events
transport.Connected += (s, e) => { /* ... */ };
transport.Disconnected += (s, e) => { /* ... */ };
transport.MessageReceived += (s, e) => 
{
    Console.WriteLine($"Topic: {e.Topic}");
    Console.WriteLine($"Payload: {e.Payload}");
};
```

---

## Nachrichtenstruktur

### I4.0 Message Frame

Jede I4.0-Nachricht besteht aus einem **Frame** (Metadaten) und **InteractionElements** (Payload).

**JSON-Beispiel:**
```json
{
  "frame": {
    "type": "inform",
    "messageId": "msg-12345-67890",
    "conversationId": "conv-abc-def",
    "sender": {
      "identification": {
        "id": "ResourceHolon_RH1"
      },
      "role": "ResourceHolon"
    },
    "receiver": {
      "identification": {
        "id": "PlanningAgent_PA1"
      },
      "role": "PlanningAgent"
    },
    "timestamp": "2024-12-08T10:30:00Z"
  },
  "interactionElements": [
    {
      "idShort": "StateMessage",
      "modelType": "SubmodelElementCollection",
      "value": [
        {
          "idShort": "ModuleState",
          "modelType": "Property",
          "valueType": "xs:string",
          "value": "Ready"
        }
      ]
    }
  ]
}
```

### Nachrichtentypen (Frame.Type)

Gemäß VDI/VDE 2193-2:

| Type | Bedeutung | Verwendung |
|------|-----------|------------|
| `inform` | Information/Update | Zustandsupdates, Logs, Benachrichtigungen |
| `consent` | Zustimmung/Akzeptanz | Skill-Anfrage akzeptiert |
| `refusal` | Ablehnung | Skill-Anfrage abgelehnt (z.B. Preconditions nicht erfüllt) |
| `failure` | Fehler während Ausführung | Skill-Fehler während der Ausführung |
| `request` | Anfrage | Skill-Anfrage, Ressourcen-Anfrage |
| `query` | Abfrage | Informationsanfrage |
| `propose` | Vorschlag | Bidding, Verhandlungen |
| `cancel` | Abbruch | Task-Abbruch |

---

## Verbindung und Konfiguration

### Basis-Konfiguration

**Minimale Konfiguration:**
```csharp
var transport = new MqttTransport("localhost", 1883, "MyAgent");
var client = new MessagingClient(transport, "default/topic");
await client.ConnectAsync();
```

**Erweiterte Konfiguration mit Authentifizierung:**
```csharp
var transport = new MqttTransport(
    brokerHost: Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost",
    brokerPort: int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT") ?? "1883"),
    clientId: $"{agentId}_{agentRole}_{Guid.NewGuid():N}",
    username: Environment.GetEnvironmentVariable("MQTT_USER"),
    password: Environment.GetEnvironmentVariable("MQTT_PASSWORD")
);

var client = new MessagingClient(transport, $"factory/agents/{agentId}");

// Connection Monitoring
client.Connected += async (s, e) =>
{
    logger.LogInformation("MQTT connected");
    await client.SubscribeAsync($"factory/agents/{agentId}/requests");
};

client.Disconnected += (s, e) =>
{
    logger.LogWarning("MQTT disconnected - attempting reconnect...");
    // Implement reconnect logic
};

await client.ConnectAsync();
```

### Connection-Pooling in MAS-BT

Im MAS-BT-Kontext wird der MessagingClient im **BTContext** gespeichert und wiederverwendet:

```csharp
// In ConnectToMessagingBrokerNode
var existingClient = Context.Get<MessagingClient>("MessagingClient");
if (existingClient != null && existingClient.IsConnected)
{
    Logger.LogDebug("Already connected, reusing existing client");
    return NodeStatus.Success;
}

// Neuen Client erstellen
var client = new MessagingClient(transport, defaultTopic);
await client.ConnectAsync();

// Im Context speichern
Context.Set("MessagingClient", client);
```

### Timeout und Retry-Logik

```csharp
public async Task<bool> ConnectWithRetryAsync(
    MessagingClient client,
    int maxRetries = 3,
    int timeoutMs = 10000)
{
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            var connectTask = client.ConnectAsync();
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logger.LogWarning($"Connection timeout (attempt {attempt}/{maxRetries})");
                continue;
            }

            await connectTask; // Propagate exceptions

            if (client.IsConnected)
            {
                Logger.LogInformation("Connected successfully");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Connection failed (attempt {attempt}/{maxRetries})");
        }

        if (attempt < maxRetries)
        {
            await Task.Delay(1000 * attempt); // Exponential backoff
        }
    }

    return false;
}
```

---

## Nachrichten Senden und Empfangen

### Nachrichten Senden (Publishing)

**Einfaches Publish:**
```csharp
var message = new I40MessageBuilder()
    .From(agentId, "ResourceHolon")
    .To("broadcast", "System")
    .WithType(I40MessageTypes.INFORM)
    .AddElement(myElement)
    .Build();

await client.PublishAsync(message, "factory/resources/rh1/state");
```

**Mit Topic-Hierarchie:**
```csharp
// Hierarchische Topics für flexible Subscription
var baseTopic = $"factory/modules/{moduleId}";

// State Updates
await client.PublishAsync(stateMessage, $"{baseTopic}/state");

// Skill Responses
await client.PublishAsync(skillResponse, $"{baseTopic}/skill/response");

// Logs
await client.PublishAsync(logMessage, $"{baseTopic}/logs");

// Errors
await client.PublishAsync(errorMessage, $"{baseTopic}/errors");
```

**Fire-and-Forget vs. Acknowledgement:**
```csharp
// Fire-and-Forget (Standard)
await client.PublishAsync(message, topic);

// Mit Fehlerbehandlung
try
{
    await client.PublishAsync(message, topic);
    Logger.LogInformation("Message sent successfully");
}
catch (Exception ex)
{
    Logger.LogError(ex, "Failed to send message");
    // Retry-Logik oder Fallback
}
```

### Nachrichten Empfangen (Subscribing)

**Topic-Subscription:**
```csharp
// Subscribe zu spezifischem Topic
await client.SubscribeAsync("factory/modules/M1/requests");

// Subscribe zu Wildcard-Topics
await client.SubscribeAsync("factory/modules/+/requests");  // Single-Level
await client.SubscribeAsync("factory/#");                   // Multi-Level
```

**Global Message Handler:**
```csharp
client.OnMessage(message =>
{
    Logger.LogDebug($"Received {message.Frame.Type} from {message.Frame.Sender.Identification.Id}");
    
    // Process based on type
    switch (message.Frame.Type)
    {
        case I40MessageTypes.REQUEST:
            HandleRequest(message);
            break;
        case I40MessageTypes.INFORM:
            HandleInform(message);
            break;
    }
});
```

**Filtered Message Handler:**
```csharp
client.OnMessage(message =>
{
    // Filter nach Nachrichtentyp
    if (message.Frame.Type != I40MessageTypes.REQUEST)
        return;

    // Filter nach Sender
    if (message.Frame.Sender.Identification.Id != "PlanningAgent_PA1")
        return;

    // Filter nach ConversationId
    var expectedConvId = Context.Get<string>("ExpectedConversationId");
    if (message.Frame.ConversationId != expectedConvId)
        return;

    // Verarbeite gefilterte Nachricht
    ProcessSkillRequest(message);
});
```

**Queue-basiertes Handling (für BT-Nodes):**
```csharp
private readonly ConcurrentQueue<I40Message> _messageQueue = new();

public async Task SubscribeToTopic(string topic)
{
    await client.SubscribeAsync(topic);
    
    client.OnMessage(message =>
    {
        if (ShouldAcceptMessage(message))
        {
            _messageQueue.Enqueue(message);
            Logger.LogDebug($"Queued message {message.Frame.MessageId}");
        }
    });
}

// In BT Node Execute()
public override Task<NodeStatus> Execute()
{
    if (_messageQueue.TryDequeue(out var message))
    {
        ProcessMessage(message);
        return NodeStatus.Success;
    }
    
    return NodeStatus.Running; // Weiter warten
}
```

### Raw JSON Handling (Workaround für AAS-Deserialisierung)

In manchen Fällen ist es notwendig, Raw JSON zu verarbeiten (z.B. bei komplexen AAS-Strukturen):

```csharp
// Subscribe auf Transport-Level
var transport = client.GetType()
    .GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance)?
    .GetValue(client) as MqttTransport;

if (transport != null)
{
    transport.MessageReceived += (sender, e) =>
    {
        if (e.Topic == targetTopic)
        {
            // Raw JSON verarbeiten
            using var doc = JsonDocument.Parse(e.Payload);
            var root = doc.RootElement;
            
            // Manuelles Parsing
            var frame = root.GetProperty("frame");
            var conversationId = frame.GetProperty("conversationId").GetString();
            
            // Payload extrahieren
            var elements = root.GetProperty("interactionElements");
            // ...
        }
    };
}
```

---

## Integration in MAS-BT

### Messaging Nodes Übersicht

MAS-BT bietet mehrere Behavior Tree Nodes für I4.0 Messaging:

| Node | Zweck | Input | Output |
|------|-------|-------|--------|
| `ConnectToMessagingBrokerNode` | Verbindung herstellen | BrokerHost, BrokerPort | MessagingClient (Context) |
| `ReadMqttSkillRequestNode` | SkillRequest empfangen | ModuleId | CurrentAction, InputParameters |
| `SendSkillResponseNode` | ActionState Update senden | ActionState, FrameType | - |
| `SendStateMessageNode` | Modulzustand senden | ModuleId, State | - |
| `SendLogMessageNode` | Log-Nachricht senden | LogLevel, Message | - |
| `SendConfigAsLogNode` | Konfiguration broadcasten | - | - |
| `WaitForMessageNode` | Auf Nachricht warten | ExpectedType, ExpectedSender | LastReceivedMessage |
| `EnableStorageChangeMqttNode` | Storage-Änderungen überwachen | - | - |

### Verwendung in Behavior Trees

**XML-Definition:**
```xml
<BehaviorTree ID="ModuleInitialization">
  <Sequence>
    <!-- 1. Messaging verbinden -->
    <ConnectToMessagingBroker 
      BrokerHost="localhost" 
      BrokerPort="1883" 
      DefaultTopic="factory/agents/messages" />
    
    <!-- 2. Konfiguration broadcasten -->
    <SendConfigAsLog />
    
    <!-- 3. Initial State senden -->
    <SendStateMessage ModuleId="{ModuleId}" />
    
    <!-- 4. Auf SkillRequest warten -->
    <ReadMqttSkillRequest ModuleId="{ModuleId}" TimeoutMs="100" />
    
    <!-- 5. SkillResponse senden -->
    <SendSkillResponse 
      ModuleId="{ModuleId}" 
      ActionState="EXECUTING" 
      FrameType="consent" />
  </Sequence>
</BehaviorTree>
```

### Services Integration

**MqttLogger (Automatisches MQTT-Logging):**
```csharp
using MAS_BT.Services;

// MqttLoggerProvider registrieren
var messagingClient = context.Get<MessagingClient>("MessagingClient");
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddProvider(new MqttLoggerProvider(
        messagingClient,
        agentId: "ResourceHolon_RH1",
        agentRole: "ResourceHolon"
    ));
});

var logger = loggerFactory.CreateLogger<MyClass>();

// Jetzt werden alle Logs automatisch auch via MQTT gesendet
logger.LogInformation("This will be sent to MQTT topic {AgentId}/logs");
logger.LogError("Error logs are also sent automatically");
```

**RemoteServerMqttNotifier (OPC UA Event-Benachrichtigung):**
```csharp
using MAS_BT.Services;

var notifier = new RemoteServerMqttNotifier(context);

// Beim RemoteServer registrieren
remoteServer.Subscribe(notifier);

// Jetzt werden OPC UA Connection-Events automatisch via MQTT publiziert:
// - OnConnectionLost() -> Error-Log via MQTT
// - OnConnectionEstablished() -> Info-Log via MQTT
// - OnStatusChange() -> Debug-Log via MQTT
```

**StorageMqttNotifier (Inventory-Änderungen):**
```csharp
using MAS_BT.Services;

var storageNotifier = new StorageMqttNotifier(
    context,
    logger,
    storageId: "Storage_S1"
);

// Bei Storage-Änderungen registrieren
storage.OnStorageChanged += storageNotifier.OnStorageChanged;

// Jetzt werden Inventory-Updates automatisch via MQTT publiziert
// auf Topic: /Storages/{StorageId}/Inventory/
```

---

## Nachrichtentypen

### 1. SkillRequest (Action-Anfrage)

**Szenario:** Planning Agent fordert Skill-Ausführung an.

**Nachrichtenfluss:**
```
PlanningAgent --> MQTT Topic: /Modules/{ModuleId}/SkillRequest/ --> ExecutionAgent
```

**Struktur:**
```json
{
  "frame": {
    "type": "request",
    "sender": { "identification": { "id": "PlanningAgent_PA1" }, "role": "PlanningAgent" },
    "receiver": { "identification": { "id": "Module_M1" }, "role": "ExecutionAgent" }
  },
  "interactionElements": [
    {
      "idShort": "Action001",
      "modelType": "SubmodelElementCollection",
      "value": [
        { "idShort": "ActionTitle", "value": "DrillHole" },
        { "idShort": "Status", "value": "planned" },
        { "idShort": "MachineName", "value": "DrillingMachine_01" },
        {
          "idShort": "InputParameters",
          "value": [
            { "idShort": "depth", "valueType": "xs:double", "value": "25.5" },
            { "idShort": "diameter", "valueType": "xs:double", "value": "8.0" },
            { "idShort": "force", "valueType": "xs:boolean", "value": "true" }
          ]
        }
      ]
    }
  ]
}
```

**MAS-BT Verarbeitung:**
```csharp
// ReadMqttSkillRequestNode extrahiert:
Context.Set("ActionTitle", "DrillHole");
Context.Set("ActionStatus", "planned");
Context.Set("MachineName", "DrillingMachine_01");
Context.Set("InputParameters", new Dictionary<string, object> 
{
    { "depth", 25.5 },
    { "diameter", 8.0 },
    { "force", true }
});
```

### 2. SkillResponse (Action-Update)

**Szenario:** Execution Agent meldet Action-Status zurück.

**Frame Types:**
- `consent`: Anfrage akzeptiert (vor Start)
- `refusal`: Anfrage abgelehnt (Preconditions nicht erfüllt)
- `inform`: Status-Update während Ausführung
- `failure`: Fehler während Ausführung

**Consent (Akzeptanz):**
```json
{
  "frame": {
    "type": "consent",
    "replyTo": "original-request-message-id",
    "conversationId": "conv-123",
    "sender": { "identification": { "id": "Module_M1_Execution_Agent" } }
  },
  "interactionElements": [
    {
      "idShort": "SkillResponse",
      "value": [
        { "idShort": "State", "value": "EXECUTING" },
        { "idShort": "ActionTitle", "value": "DrillHole" }
      ]
    }
  ]
}
```

**Refusal (Ablehnung):**
```json
{
  "frame": {
    "type": "refusal",
    "replyTo": "original-request-message-id"
  },
  "interactionElements": [
    {
      "idShort": "SkillResponse",
      "value": [
        { "idShort": "State", "value": "ERROR" },
        { "idShort": "LogMessage", "value": "Preconditions not satisfied: Tool not available" },
        { "idShort": "Step", "value": "CheckPreconditions" }
      ]
    }
  ]
}
```

**Inform (Status-Update):**
```json
{
  "frame": {
    "type": "inform",
    "conversationId": "conv-123"
  },
  "interactionElements": [
    {
      "idShort": "SkillResponse",
      "value": [
        { "idShort": "State", "value": "DONE" },
        { "idShort": "ActionTitle", "value": "DrillHole" },
        {
          "idShort": "FinalResultData",
          "value": [
            { "idShort": "actualDepth", "value": "25.3" },
            { "idShort": "executionTime", "value": "12.5" }
          ]
        },
        { "idShort": "SuccessfulExecutionsCount", "value": "1" }
      ]
    }
  ]
}
```

### 3. StateMessage (Modulzustand)

**Szenario:** Execution Agent broadcastet Modulzustand.

**Topic:** `/Modules/{ModuleId}/State/`

**Struktur:**
```json
{
  "frame": {
    "type": "inform",
    "sender": { "identification": { "id": "Module_M1_Execution_Agent" } },
    "receiver": { "identification": { "id": "Broadcast" } }
  },
  "interactionElements": [
    {
      "idShort": "StateMessage",
      "value": [
        { "idShort": "IsLocked", "valueType": "xs:boolean", "value": "false" },
        { "idShort": "IsReady", "valueType": "xs:boolean", "value": "true" },
        { "idShort": "ModuleState", "value": "Ready" },
        { "idShort": "HasError", "valueType": "xs:boolean", "value": "false" },
        { "idShort": "StartupSkillRunning", "valueType": "xs:boolean", "value": "false" }
      ]
    }
  ]
}
```

### 4. LogMessage

**Szenario:** Verteiltes Logging über MQTT.

**Topic:** `{AgentId}/logs`

**Struktur:**
```json
{
  "frame": {
    "type": "inform",
    "sender": { "identification": { "id": "ResourceHolon_RH1" } }
  },
  "interactionElements": [
    {
      "idShort": "LogMessage",
      "value": [
        { "idShort": "LogLevel", "value": "INFO" },
        { "idShort": "Message", "value": "Skill execution started" },
        { "idShort": "AgentRole", "value": "ResourceHolon" },
        { "idShort": "AgentId", "value": "ResourceHolon_RH1" },
        { "idShort": "Timestamp", "value": "2024-12-08T10:30:15Z" }
      ]
    }
  ]
}
```

**LogLevels:**
- `TRACE`, `DEBUG`, `INFO`, `WARNING`, `ERROR`, `CRITICAL`, `FATAL`

### 5. Inventory Update

**Szenario:** Storage meldet Bestandsänderungen.

**Topic:** `/Storages/{StorageId}/Inventory/`

**Struktur:**
```json
{
  "frame": {
    "type": "inform",
    "sender": { "identification": { "id": "Storage_S1" } }
  },
  "interactionElements": [
    {
      "idShort": "InventoryUpdate",
      "value": [
        { "idShort": "ProductName", "value": "Screw_M8" },
        { "idShort": "Quantity", "valueType": "xs:integer", "value": "47" },
        { "idShort": "ChangeType", "value": "Consumed" },
        { "idShort": "ChangedBy", "value": "Module_M1" }
      ]
    }
  ]
}
```

---

## AAS-basierte Nachrichtenformate

### Integration mit AAS Sharp Client

I4.0 Sharp Messaging nutzt **BaSyx.Models.AdminShell** für strukturierte Payloads:

```csharp
using BaSyx.Models.AdminShell;
using AasSharpClient.Models;
using AasSharpClient.Models.Messages;

// StateMessage (aus AAS Sharp Client Library)
var stateMessage = new StateMessage(
    isLocked: false,
    isReady: true,
    moduleState: "Ready",
    hasError: false,
    startupSkillRunning: false
);

// LogMessage
var logMessage = new LogMessage(
    logLevel: LogMessage.LogLevel.Info,
    message: "Execution started",
    agentRole: "ResourceHolon",
    agentId: "RH1"
);

// SkillResponseMessage
var skillResponse = new SkillResponseMessage(
    state: "DONE",
    statusValue: "done",
    actionTitle: "DrillHole",
    machineName: "DrillingMachine_01",
    step: null,
    inputParameters: new Dictionary<string, string> { { "depth", "25.5" } },
    finalResultData: new Dictionary<string, object> { { "actualDepth", 25.3 } },
    logMessage: null,
    successfulExecutionsCount: 1
);

// In I40Message einbetten
var message = new I40MessageBuilder()
    .From(agentId, role)
    .To(receiverId, receiverRole)
    .WithType(I40MessageTypes.INFORM)
    .AddElement(skillResponse)
    .Build();
```

### SubmodelElement Hierarchie

AAS-basierte Nachrichten nutzen die SubmodelElement-Hierarchie:

```
SubmodelElementCollection (Action001)
├── Property (ActionTitle = "DrillHole")
├── Property (Status = "planned")
├── Property (MachineName = "DrillingMachine_01")
└── SubmodelElementCollection (InputParameters)
    ├── Property (depth = 25.5, valueType: xs:double)
    ├── Property (diameter = 8.0, valueType: xs:double)
    └── Property (force = true, valueType: xs:boolean)
```

**Manuelles Erstellen:**
```csharp
// Collection erstellen
var action = new SubmodelElementCollection("Action001");

// Properties hinzufügen
var titleProp = new Property<string>("ActionTitle");
titleProp.Value = new PropertyValue<string>("DrillHole");
action.Add(titleProp);

var statusProp = new Property<string>("Status");
statusProp.Value = new PropertyValue<string>("planned");
action.Add(statusProp);

// Nested Collection
var inputParams = new SubmodelElementCollection("InputParameters");

var depthProp = new Property<double>("depth");
depthProp.ValueType = "xs:double";
depthProp.Value = new PropertyValue<double>(25.5);
inputParams.Add(depthProp);

action.Add(inputParams);
```

### Typed vs. String Values

MAS-BT unterstützt typisierte InputParameters:

```csharp
// Typisierte Parameter (empfohlen)
var inputParams = new Dictionary<string, object>
{
    { "depth", 25.5 },           // double
    { "diameter", 8.0 },         // double
    { "force", true },           // bool
    { "toolId", "T-123" }        // string
};

// Konvertierung zu AAS Properties
var inputModel = InputParameters.FromTypedValues(inputParams);

// Beim Empfangen: Automatisches Type-Mapping basierend auf valueType
// xs:double -> double
// xs:integer -> int
// xs:boolean -> bool
// xs:string -> string (default)
```

---

## Best Practices

### 1. Connection Management

✅ **DO:**
- Wiederverwendung des MessagingClient über BTContext
- Connection-Pooling implementieren
- Graceful Disconnect bei Shutdown

❌ **DON'T:**
- Pro Nachricht neue Verbindung aufbauen
- Verbindungen ohne Cleanup offen lassen

```csharp
// GOOD: Reuse from Context
var client = Context.Get<MessagingClient>("MessagingClient");
if (client == null || !client.IsConnected)
{
    // Reconnect logic
}

// BAD: Create new client every time
var client = new MessagingClient(new MqttTransport(...), ...);
```

### 2. Topic-Struktur

✅ **DO:**
- Hierarchische, semantische Topics verwenden
- Agent-spezifische Topics
- Wildcard-Subscriptions für Broadcast

❌ **DON'T:**
- Flache Topic-Struktur ohne Hierarchie
- Zu generische Topics

```csharp
// GOOD
/factory/modules/M1/requests
/factory/modules/M1/responses
/factory/resources/RH1/state
/factory/agents/+/logs         // Wildcard subscription

// BAD
/messages
/data
/stuff
```

### 3. Error Handling

✅ **DO:**
- Try-Catch um PublishAsync
- Timeout bei ConnectAsync
- Logging aller Fehler

```csharp
try
{
    await client.PublishAsync(message, topic);
}
catch (Exception ex)
{
    Logger.LogError(ex, "Failed to publish to {Topic}", topic);
    // Fallback oder Retry
    return NodeStatus.Failure;
}
```

### 4. Message ID Management

✅ **DO:**
- Eindeutige MessageIds generieren (GUID)
- ConversationIds für Request-Reply beibehalten
- ReplyTo für Antworten setzen

```csharp
// Request
var requestId = Guid.NewGuid().ToString();
var conversationId = Guid.NewGuid().ToString();

var request = new I40MessageBuilder()
    .WithMessageId(requestId)
    .WithConversationId(conversationId)
    // ...
    .Build();

// Response
var response = new I40MessageBuilder()
    .WithMessageId(Guid.NewGuid().ToString())
    .WithConversationId(conversationId)  // Same conversation!
    .ReplyingTo(requestId)               // Reference original
    // ...
    .Build();
```

### 5. Serialization

✅ **DO:**
- Typisierte Messages verwenden (StateMessage, LogMessage, etc.)
- ValueType explizit setzen bei Properties
- Case-insensitive Dictionaries für InputParameters

```csharp
// GOOD: Typisiert mit valueType
var inputParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
{
    { "Depth", 25.5 },     // Case-insensitive
    { "depth", 8.0 }       // Überschreibt "Depth"
};

// Property mit explizitem valueType
var prop = new Property<double>("depth");
prop.ValueType = "xs:double";
prop.Value = new PropertyValue<double>(25.5);
```

### 6. Lifecycle Management

✅ **DO:**
- Subscribe in OnInitialize/OnEnter
- Unsubscribe in OnAbort/OnExit
- Queue leeren bei OnReset

```csharp
private ConcurrentQueue<I40Message> _queue = new();
private bool _subscribed = false;

public override async Task<NodeStatus> Execute()
{
    if (!_subscribed)
    {
        await client.SubscribeAsync(topic);
        _subscribed = true;
    }
    
    // Process messages...
}

public override async Task OnAbort()
{
    if (_subscribed)
    {
        await client.UnsubscribeAsync(topic);
        _subscribed = false;
    }
    _queue.Clear();
}
```

---

## Troubleshooting

### Problem: Verbindung schlägt fehl

**Symptom:**
```
ConnectAsync() wirft Exception oder Timeout
```

**Mögliche Ursachen:**
1. MQTT Broker nicht erreichbar
2. Falsche Host/Port-Konfiguration
3. Firewall blockiert Port 1883
4. Authentifizierung fehlgeschlagen

**Lösungen:**
```bash
# Broker-Verfügbarkeit prüfen
mosquitto -v -p 1883  # Lokaler Broker

# Telnet-Test
telnet localhost 1883

# MQTT-Client testen
mosquitto_pub -h localhost -p 1883 -t test -m "hello"
mosquitto_sub -h localhost -p 1883 -t test
```

### Problem: Nachrichten werden nicht empfangen

**Symptom:**
```
OnMessage callback wird nicht aufgerufen
```

**Debugging:**
```csharp
// 1. Verbindungsstatus prüfen
if (!client.IsConnected)
{
    Logger.LogError("Client not connected!");
}

// 2. Subscription prüfen
Logger.LogInformation("Subscribing to {Topic}", topic);
await client.SubscribeAsync(topic);

// 3. Global Callback registrieren
client.OnMessage(msg =>
{
    Logger.LogInformation("Received ANY message from {Sender} on topic {Topic}",
        msg.Frame.Sender.Identification.Id,
        "unknown"); // Topic ist im Message nicht enthalten
});

// 4. Raw Transport-Level prüfen
transport.MessageReceived += (s, e) =>
{
    Logger.LogInformation("Raw MQTT: Topic={Topic}, Payload={Payload}",
        e.Topic, e.Payload);
};
```

### Problem: Deserialisierung schlägt fehl

**Symptom:**
```
Exception beim Parsen von InteractionElements
```

**Workaround: Raw JSON Parsing**
```csharp
transport.MessageReceived += (s, e) =>
{
    try
    {
        using var doc = JsonDocument.Parse(e.Payload);
        var root = doc.RootElement;
        
        // Manuelles Parsing
        if (root.TryGetProperty("frame", out var frame))
        {
            var msgType = frame.GetProperty("type").GetString();
            Logger.LogInformation("Message type: {Type}", msgType);
        }
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to parse raw JSON");
    }
};
```

### Problem: Topic-Mismatch

**Symptom:**
```
Nachrichten kommen an, werden aber nicht verarbeitet
```

**Debugging:**
```csharp
// Log alle empfangenen Topics
transport.MessageReceived += (s, e) =>
{
    Logger.LogInformation("Received on topic: '{Topic}'", e.Topic);
    
    // Vergleiche mit erwartetem Topic
    var expectedTopic = $"/Modules/{moduleId}/SkillRequest/";
    if (e.Topic == expectedTopic)
    {
        Logger.LogInformation("Topic matches!");
    }
    else
    {
        Logger.LogWarning("Topic mismatch! Expected: {Expected}, Got: {Actual}",
            expectedTopic, e.Topic);
    }
};
```

### Problem: ConversationId fehlt in Antworten

**Symptom:**
```
Planning Agent kann Antworten nicht zuordnen
```

**Lösung:**
```csharp
// Request: ConversationId speichern
var conversationId = message.Frame.ConversationId;
Context.Set("ConversationId", conversationId);
Context.Set("OriginalMessageId", message.Frame.MessageId);

// Response: ConversationId wiederverwenden
var storedConvId = Context.Get<string>("ConversationId");
var storedMsgId = Context.Get<string>("OriginalMessageId");

var response = new I40MessageBuilder()
    .WithConversationId(storedConvId)  // IMPORTANT!
    .ReplyingTo(storedMsgId)
    // ...
    .Build();
```

### Problem: Memory Leak bei Subscriptions

**Symptom:**
```
Speicherverbrauch steigt kontinuierlich
```

**Lösung:**
```csharp
// Immer Unsubscribe in OnAbort/OnReset
public override async Task OnAbort()
{
    if (_subscribed)
    {
        try
        {
            await client.UnsubscribeAsync(topic);
            Logger.LogInformation("Unsubscribed from {Topic}", topic);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to unsubscribe");
        }
        _subscribed = false;
    }
    
    // Queue leeren
    _messageQueue.Clear();
}
```

---

## Beispiele

### Beispiel 1: Einfacher Request-Reply

**Planning Agent sendet SkillRequest:**
```csharp
// Planning Agent (Sender)
var request = new I40MessageBuilder()
    .From("PlanningAgent_PA1", "PlanningAgent")
    .To("Module_M1", "ExecutionAgent")
    .WithType(I40MessageTypes.REQUEST)
    .WithConversationId("conv-drill-123")
    .WithMessageId("req-001")
    .AddElement(CreateSkillRequestAction("DrillHole", new Dictionary<string, object>
    {
        { "depth", 25.5 },
        { "diameter", 8.0 }
    }))
    .Build();

await client.PublishAsync(request, "/Modules/M1/SkillRequest/");
Logger.LogInformation("Sent SkillRequest conv-drill-123");
```

**Execution Agent empfängt und antwortet:**
```csharp
// Execution Agent (Receiver)
await client.SubscribeAsync("/Modules/M1/SkillRequest/");

client.OnMessage(async message =>
{
    if (message.Frame.Type != I40MessageTypes.REQUEST)
        return;
    
    var conversationId = message.Frame.ConversationId;
    var originalMsgId = message.Frame.MessageId;
    
    // Process request...
    var actionTitle = ExtractActionTitle(message);
    Logger.LogInformation("Processing SkillRequest: {Action}", actionTitle);
    
    // Send consent
    var consent = new I40MessageBuilder()
        .From("Module_M1_Execution_Agent", "ExecutionAgent")
        .To(message.Frame.Sender.Identification.Id, "PlanningAgent")
        .WithType(I40MessageTypes.CONSENT)
        .WithConversationId(conversationId)  // Same conversation!
        .ReplyingTo(originalMsgId)
        .AddElement(CreateSkillResponse("EXECUTING", actionTitle))
        .Build();
    
    await client.PublishAsync(consent, "/Modules/M1/SkillResponse/");
    
    // Execute skill...
    await ExecuteSkillAsync(actionTitle);
    
    // Send done
    var done = new I40MessageBuilder()
        .From("Module_M1_Execution_Agent", "ExecutionAgent")
        .To(message.Frame.Sender.Identification.Id, "PlanningAgent")
        .WithType(I40MessageTypes.INFORM)
        .WithConversationId(conversationId)
        .ReplyingTo(originalMsgId)
        .AddElement(CreateSkillResponse("DONE", actionTitle))
        .Build();
    
    await client.PublishAsync(done, "/Modules/M1/SkillResponse/");
});
```

### Beispiel 2: State Broadcasting

**Resource Holon sendet periodisch State Updates:**
```csharp
// State Broadcasting mit Timer
var stateTimer = new System.Timers.Timer(5000); // Alle 5 Sekunden
stateTimer.Elapsed += async (s, e) =>
{
    var stateMessage = new StateMessage(
        isLocked: Context.Get<bool>("module_M1_locked"),
        isReady: Context.Get<bool>("module_M1_ready"),
        moduleState: Context.Get<string>("ModuleState_M1") ?? "Unknown",
        hasError: Context.Get<bool>("module_M1_has_error"),
        startupSkillRunning: Context.Get<bool>("startupSkillRunning")
    );
    
    var message = new I40MessageBuilder()
        .From("Module_M1_Execution_Agent", "ExecutionAgent")
        .To("Broadcast", "System")
        .WithType(I40MessageTypes.INFORM)
        .AddElement(stateMessage)
        .Build();
    
    try
    {
        await client.PublishAsync(message, "/Modules/M1/State/");
        Logger.LogDebug("State broadcast sent");
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to broadcast state");
    }
};

stateTimer.Start();
```

**Planning Agent subscribed zu allen Module States:**
```csharp
// Planning Agent überwacht alle Module
await client.SubscribeAsync("/Modules/+/State/");  // Wildcard!

var moduleStates = new ConcurrentDictionary<string, StateMessage>();

client.OnMessage(message =>
{
    if (message.Frame.Type != I40MessageTypes.INFORM)
        return;
    
    // Extrahiere StateMessage
    var stateElement = message.InteractionElements
        .FirstOrDefault(e => e.IdShort == "StateMessage");
    
    if (stateElement != null)
    {
        var senderId = message.Frame.Sender.Identification.Id;
        
        // Parse StateMessage
        var isReady = ExtractBoolProperty(stateElement, "IsReady");
        var moduleState = ExtractStringProperty(stateElement, "ModuleState");
        
        Logger.LogInformation("Module {Id}: State={State}, Ready={Ready}",
            senderId, moduleState, isReady);
        
        // Update local cache
        // moduleStates[senderId] = ...
    }
});
```

### Beispiel 3: Fehlerbehandlung mit Refusal

**Execution Agent lehnt Anfrage ab (Preconditions nicht erfüllt):**
```csharp
// Precondition Check im Execution Agent
var toolAvailable = CheckToolAvailability("T-123");
var materialAvailable = CheckMaterialStock("MAT-456", requiredQuantity: 10);

if (!toolAvailable || !materialAvailable)
{
    var reason = !toolAvailable 
        ? "Required tool T-123 not available"
        : "Insufficient material stock for MAT-456";
    
    var refusal = new I40MessageBuilder()
        .From("Module_M1_Execution_Agent", "ExecutionAgent")
        .To(requestSenderId, "PlanningAgent")
        .WithType(I40MessageTypes.REFUSAL)
        .WithConversationId(conversationId)
        .ReplyingTo(originalMessageId)
        .AddElement(new SkillResponseMessage(
            state: "ERROR",
            statusValue: "error",
            actionTitle: actionTitle,
            machineName: "DrillingMachine_01",
            step: "CheckPreconditions",
            inputParameters: null,
            finalResultData: null,
            logMessage: reason,
            successfulExecutionsCount: null
        ))
        .Build();
    
    await client.PublishAsync(refusal, "/Modules/M1/SkillResponse/");
    
    Logger.LogWarning("SkillRequest refused: {Reason}", reason);
    return NodeStatus.Failure;
}

// Preconditions erfüllt -> consent senden
// ...
```

### Beispiel 4: Logging Integration

**Automatisches MQTT-Logging über MqttLogger:**
```csharp
// Setup in Main/Startup
var messagingClient = context.Get<MessagingClient>("MessagingClient");

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
    
    // MqttLogger hinzufügen
    builder.AddProvider(new MqttLoggerProvider(
        messagingClient,
        agentId: "ResourceHolon_RH1",
        agentRole: "ResourceHolon"
    ));
});

var logger = loggerFactory.CreateLogger<ModuleController>();

// Jetzt automatisch via MQTT geloggt:
logger.LogInformation("Module initialized successfully");
logger.LogWarning("Temperature threshold exceeded: {Temp}°C", 85.5);
logger.LogError("Skill execution failed: {Error}", ex.Message);

// Logs erscheinen auf Topic: ResourceHolon_RH1/logs
```

---

## Weiterführende Ressourcen

### Referenzen

- **VDI/VDE 2193-2**: Standard für Nachrichtenkommunikation in Multi-Agenten-Systemen
- **Asset Administration Shell (AAS)**: [https://www.plattform-i40.de/](https://www.plattform-i40.de/)
- **MQTT Protocol**: [https://mqtt.org/](https://mqtt.org/)
- **BaSyx SDK**: [https://wiki.eclipse.org/BaSyx](https://wiki.eclipse.org/BaSyx)

### Verwandte MAS-BT Dokumentation

- `CONFIGURATION_NODES.md`: Konfigurationsnodes inkl. ConnectToMessagingBroker
- `MONITORING_AND_SKILL_NODES.md`: Skill-Ausführung und Monitoring
- `EXECUTION_AGENT_TODO.md`: Execution Queue und Action Processing
- `README.md`: Systemarchitektur und Holon-Konzepte

### Code-Referenzen in MAS-BT

**Services:**
- `Services/RemoteServerMqttNotifier.cs`: OPC UA Event-Benachrichtigung
- `Services/MqttLogger.cs`: Automatisches MQTT-Logging
- `Services/StorageMqttNotifier.cs`: Inventory-Update-Broadcasting

**Nodes:**
- `Nodes/Messaging/ConnectToMessagingBrokerNode.cs`
- `Nodes/Messaging/ReadMqttSkillRequestNode.cs`
- `Nodes/Messaging/SendSkillResponseNode.cs`
- `Nodes/Messaging/SendStateMessageNode.cs`
- `Nodes/Messaging/SendLogMessageNode.cs`
- `Nodes/Messaging/WaitForMessageNode.cs`

### Externe Abhängigkeiten

```xml
<!-- MAS-BT.csproj -->
<ProjectReference Include="../I4.0-Sharp-Messaging/I40Sharp.Messaging/I40Sharp.Messaging.csproj" />
<ProjectReference Include="../AAS-Sharp-Client/AAS Sharp Client.csproj" />
```

---

## Zusammenfassung

Der **I4.0 Sharp Messaging Client** ist die zentrale Komponente für die standardisierte, AAS-konforme Kommunikation in MAS-BT. Durch die Integration mit MQTT und die Verwendung von typisierenden Nachrichtenformaten ermöglicht er:

- **Verteilte Holonische Kommunikation**: Planning ↔ Execution ↔ Resource ↔ Transport Agents
- **Standardisierte Semantik**: VDI/VDE 2193-2 konforme Nachrichtenformate
- **Flexible Topologie**: Hierarchische Topics, Wildcards, Broadcasting
- **Robuste Fehlerbehandlung**: Consent/Refusal/Failure Patterns
- **Seamless AAS-Integration**: Direkte Verwendung von AAS SubmodelElements
- **Production-Ready**: Connection-Pooling, Retry-Logic, Distributed Logging

Die vollständige Integration in Behavior Trees macht I4.0 Messaging zu einem natürlichen, deklarativen Teil der Agentenlogik und ermöglicht testbare, wartbare und skalierbare Multi-Agenten-Systeme für flexible Produktion.

---

*Letzte Aktualisierung: 2024-12-08*

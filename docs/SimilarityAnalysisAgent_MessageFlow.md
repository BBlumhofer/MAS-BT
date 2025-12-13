# SimilarityAnalysisAgent - Message Flow

## Request/Response Flow

### 1. Request kommt rein
**Topic**: `/phuket/SimilarityAnalysisAgent_phuket/CalcSimilarity`

```json
{
  "frame": {
    "sender": {
      "identification": { "id": "DispatchingAgent_phuket" },
      "role": { "name": "DispatchingAgent" }
    },
    "receiver": {
      "identification": { "id": "SimilarityAnalysisAgent_phuket" },
      "role": { "name": "AIAgent" }
    },
    "type": "calcSimilarity",
    "conversationId": "f47b14f5-cfe3-43ec-8eb3-037ae71c3317"  ← Diese ID wird übernommen!
  },
  "interactionElements": [
    { "idShort": "Capability_0", "value": "Assemble" },
    { "idShort": "Capability_1", "value": "Screw" }
  ]
}
```

### 2. Agent verarbeitet
1. ✅ WaitForMessage: Empfängt auf `/phuket/SimilarityAnalysisAgent_phuket/CalcSimilarity`
2. ✅ CalcEmbedding: Holt Embeddings von Ollama
3. ✅ CalcCosineSimilarity: 
   - Berechnet Similarity
   - Erstellt Response mit **gleicher ConversationId**
   - Setzt Receiver = Original Sender (DispatchingAgent_phuket)
4. ✅ SendResponseMessage: Sendet zurück auf **Sender-Topic**

### 3. Response geht raus
**Topic**: `/phuket/DispatchingAgent_phuket/calcSimilarity` ← Zurück zum Sender!

```json
{
  "frame": {
    "sender": {
      "identification": { "id": "SimilarityAnalysisAgent_phuket" },
      "role": { "name": "AIAgent" }
    },
    "receiver": {
      "identification": { "id": "DispatchingAgent_phuket" },
      "role": { "name": "DispatchingAgent" }
    },
    "type": "informConfirm",
    "conversationId": "f47b14f5-cfe3-43ec-8eb3-037ae71c3317"  ← GLEICHE ID!
  },
  "interactionElements": [
    {
      "idShort": "CosineSimilarity",
      "modelType": "Property",
      "valueType": "double",
      "value": 0.468055
    }
  ]
}
```

## Wichtige Änderungen

### SendResponseMessageNode - Automatische Topic-Ableitung

```csharp
// OHNE explizites Topic in BT:
<SendResponseMessage name="SendSimilarityResponse" />

// → Sendet automatisch zurück an:
//   /{Namespace}/{OriginalSender}/{MessageType}
//   Beispiel: /phuket/DispatchingAgent_phuket/calcSimilarity
```

### CalcCosineSimilarityNode - ConversationId Übernahme

```csharp
var conversationId = requestMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();
// ↑ Nimmt IMMER die ConversationId vom Request!

var builder = new I40MessageBuilder()
    .WithConversationId(conversationId);  // Gleiche ID wie Request
```

## Testing

### Mit MQTT:
```bash
# 1. Agent starten
dotnet run -- --configPath configs/specific_configs/Module_configs/phuket/SimilarityAnalysisAgent.json

# 2. Request senden (mit mosquitto_pub)
mosquitto_pub -t "/phuket/SimilarityAnalysisAgent_phuket/CalcSimilarity" \
  -m '{"frame":{"sender":{"identification":{"id":"DispatchingAgent_phuket"},"role":{"name":"DispatchingAgent"}},"receiver":{"identification":{"id":"SimilarityAnalysisAgent_phuket"},"role":{"name":"AIAgent"}},"type":"calcSimilarity","conversationId":"test-123"},"interactionElements":[{"idShort":"Capability_0","kind":"Instance","modelType":"Property","valueType":"string","value":"Assemble"},{"idShort":"Capability_1","kind":"Instance","modelType":"Property","valueType":"string","value":"Screw"}]}'

# 3. Response abhören
mosquitto_sub -t "/phuket/DispatchingAgent_phuket/#" -v
```

### Erwartetes Ergebnis:
- Response kommt auf `/phuket/DispatchingAgent_phuket/calcSimilarity`
- ConversationId = "test-123" (gleiche wie Request)
- CosineSimilarity = ~0.468 (für Assemble vs Screw)

## ✅ Zusammenfassung

1. ✅ Response geht zurück auf das **CalcSimilarity-Topic des Senders**
2. ✅ ConversationId wird **vom Request übernommen**
3. ✅ Automatische Topic-Ableitung basierend auf Sender
4. ✅ Konsistent mit Request/Response-Pattern

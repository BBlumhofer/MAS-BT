# SimilarityAnalysisAgent

Ein AI-Agent für semantische Ähnlichkeitsanalyse zwischen InteractionElements mittels Embeddings und Kosinusähnlichkeit.

## Übersicht

Der **SimilarityAnalysisAgent** ist ein spezialisierter AI-Agent, der:
- Sich beim DispatchingAgent mit der Capability **CalcSimilarity** registriert
- I4.0-Messages vom Typ `calcSimilarity` empfängt
- Text aus InteractionElements extrahiert
- Embeddings über Ollama erzeugt
- Kosinusähnlichkeit zwischen den Embeddings berechnet
- Eine I4.0-Response mit dem Similarity-Wert zurücksendet

## Architektur

### Komponenten

```
SimilarityAnalysisAgent
├── Behavior Tree (Trees/SimilarityAnalysisAgent.bt.xml)
├── Config (configs/specific_configs/Module_configs/phuket/SimilarityAnalysisAgent.json)
└── Nodes
    ├── CalcEmbeddingNode (Nodes/Common/CalcEmbeddingNode.cs)
    ├── CalcCosineSimilarityNode (Nodes/Common/CalcCosineSimilarityNode.cs)
    └── SendResponseMessageNode (Nodes/Messaging/SendResponseMessageNode.cs)
```

### Workflow

```
┌─────────────────────────────────────────────────┐
│ 1. WaitForMessage (calcSimilarity)              │
└─────────────────┬───────────────────────────────┘
                  │
┌─────────────────▼───────────────────────────────┐
│ 2. CalcEmbedding                                │
│    - Extrahiert Text aus InteractionElements    │
│    - Validiert Anzahl (genau 2 erwartet)        │
│    - Ruft Ollama API für Embeddings auf         │
└─────────────────┬───────────────────────────────┘
                  │
┌─────────────────▼───────────────────────────────┐
│ 3. CalcCosineSimilarity                         │
│    - Berechnet Kosinusähnlichkeit               │
│    - Erstellt Response-Message                  │
└─────────────────┬───────────────────────────────┘
                  │
┌─────────────────▼───────────────────────────────┐
│ 4. SendResponseMessage (informConfirm)          │
│    - Sendet Similarity-Wert zurück              │
└─────────────────────────────────────────────────┘
```

## Konfiguration

### Basis-Konfiguration

```json
{
  "Agent": {
    "AgentId": "SimilarityAnalysisAgent_phuket",
    "Role": "AIAgent",
    "AgentType": "SimilarityAnalysisAgent",
    "ParentAgent": "DispatchingAgent_phuket",
    "Capabilities": ["CalcSimilarity"]
  },
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "Model": "nomic-embed-text",
    "Timeout": 30000
  },
  "SimilarityAnalysis": {
    "MaxInteractionElements": 2,
    "MinSimilarityThreshold": 0.0
  }
}
```

### Ollama Setup

1. **Ollama installieren** (falls noch nicht vorhanden):
   ```bash
   curl -fsSL https://ollama.com/install.sh | sh
   ```

2. **Ollama starten**:
   ```bash
   ollama serve
   ```

3. **Embedding-Modell herunterladen**:
   ```bash
   ollama pull nomic-embed-text
   ```

4. **Verfügbare Modelle anzeigen**:
   ```bash
   ollama list
   ```

## Message-Format

### Request (calcSimilarity)

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
    "conversationId": "f47b14f5-cfe3-43ec-8eb3-037ae71c3317"
  },
  "interactionElements": [
    {
      "idShort": "Capability_0",
      "kind": "Instance",
      "modelType": "Property",
      "valueType": "string",
      "value": "Assemble"
    },
    {
      "idShort": "Capability_1",
      "kind": "Instance",
      "modelType": "Property",
      "valueType": "string",
      "value": "Screw"
    }
  ]
}
```

### Response (informConfirm)

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
    "conversationId": "f47b14f5-cfe3-43ec-8eb3-037ae71c3317"
  },
  "interactionElements": [
    {
      "idShort": "CosineSimilarity",
      "modelType": "Property",
      "valueType": "double",
      "value": 0.7234
    }
  ]
}
```

## Verwendung

### Agent starten

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT

# Mit spezifischer Config
dotnet run -- \
  --configPath configs/specific_configs/Module_configs/phuket/SimilarityAnalysisAgent.json
```

### Tests ausführen

```bash
# Alle Tests ausführen
./run_similarity_test.sh

# Oder nur einzelne Tests
dotnet test --filter "FullyQualifiedName~SimilarityAnalysisTests"
```

## Tests

Die Test-Suite umfasst:

1. **CalcEmbedding_WithTwoProperties_ExtractsTextAndGetsEmbeddings**
   - Testet die Extraktion von Text aus InteractionElements
   - Validiert Ollama-Integration
   - Prüft Embedding-Dimensionen

2. **CalcCosineSimilarity_WithTwoEmbeddings_CalculatesSimilarity**
   - Testet die Kosinusähnlichkeits-Berechnung
   - Validiert Response-Message-Erzeugung
   - Prüft Similarity-Wert im Bereich [-1, 1]

3. **SimilarityAnalysis_EndToEnd_WithRealMessage**
   - Vollständiger End-to-End-Test
   - Verwendet echte Ollama-API (falls verfügbar)
   - Testet kompletten Workflow

4. **CalcEmbedding_WithWrongNumberOfElements_Fails**
   - Validierungstest für falsche Anzahl von Elements
   - Erwartet Failure-Status

### Testdateien

- **Test-Message**: `tests/TestFiles/similarity_request_assemble_screw.json`
- **Test-Code**: `tests/SimilarityAnalysisTests.cs`

## Beispiel-Ähnlichkeiten

Beispiele für Capability-Vergleiche (mit nomic-embed-text):

| Capability 1 | Capability 2 | Similarity | Interpretation |
|-------------|-------------|------------|----------------|
| Assemble    | Screw       | ~0.65-0.75 | Mittlere Ähnlichkeit (verwandt) |
| Assemble    | Assemble    | 1.00       | Identisch |
| Assemble    | Transport   | ~0.30-0.45 | Geringe Ähnlichkeit |
| Screw       | Bolt        | ~0.80-0.90 | Hohe Ähnlichkeit (sehr verwandt) |

*Hinweis: Werte können je nach Modell und Version variieren.*

## Troubleshooting

### Ollama nicht verfügbar

**Problem**: Tests schlagen fehl mit "Ollama not available"

**Lösung**:
```bash
# Ollama starten
ollama serve

# Model herunterladen
ollama pull nomic-embed-text

# Testen
curl http://localhost:11434/api/tags
```

### Falsche Anzahl InteractionElements

**Problem**: Agent gibt Fehler bei falscher Element-Anzahl

**Lösung**: Sicherstellen, dass genau 2 InteractionElements in der Message sind:
```json
"interactionElements": [
  { "value": "Element1" },
  { "value": "Element2" }
]
```

### Connection-Fehler zu MQTT

**Problem**: Agent kann sich nicht mit MQTT verbinden

**Lösung**:
```bash
# MQTT-Broker starten (z.B. Mosquitto)
mosquitto -v

# Oder Config anpassen
nano configs/specific_configs/Module_configs/phuket/SimilarityAnalysisAgent.json
```

## Erweiterungen

Mögliche zukünftige Erweiterungen:

- [ ] Support für mehr als 2 InteractionElements (Paarweise Vergleiche)
- [ ] Alternative Similarity-Metriken (Euclidean, Manhattan)
- [ ] Caching von Embeddings zur Performance-Optimierung
- [ ] Batch-Processing mehrerer Similarity-Requests
- [ ] Alternative Embedding-Modelle (sentence-transformers, etc.)
- [ ] Threshold-basierte Filterung (nur Similarities > X zurückgeben)

## Lizenz

Teil des MAS-BT Projekts.

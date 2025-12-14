# Neo4j-basiertes Capability Property Matching (PlanningAgent)

## Ziel
Beim Capability Matchmaking soll zuerst eine semantische Zuordnung zwischen **required** und **offered** Properties gefunden werden.
Dazu wird **Cosine Similarity** auf bereits vorhandenen **Embeddings** verwendet.
Wenn eine Zuordnung gefunden ist, werden anschließend die **Werte/Constraints** streng validiert (Range/List/Value, Wildcard `*`).

## Datenquellen

### Required (aus CfP)
- Quelle: `CapabilityRequestContext` / `CapabilityContainer` (aus `ParseCapabilityRequest`).
- Format: AAS-ähnliche Containerstruktur.
- Embeddings:
  - Falls ein `Property` mit `idShort=embedding` im Container vorhanden ist, wird dieses Embedding direkt genutzt.
  - Falls kein Embedding vorhanden ist: Fallback über `ITextEmbeddingProvider` (z.B. Ollama) via `BuildIdentityText()`.

### Offered (aus Neo4j)
- Quelle: Neo4j Graph.
- Query (Konzept):
  - `(Asset)-[:PROVIDES_CAPABILITY]->(Capability)-[:HAS_PROPERTY]->(CapabilityPropertyContainer)`
  - Values/Range/List sind als Sub-Properties unterhalb des Containers gespeichert.
- Embeddings:
  - `CapabilityPropertyContainer.embedding` liegt als **komma-separierte Float-Liste** vor.
  - Der PlanningAgent lädt diese Embeddings und verwendet sie direkt.
  - Optional: falls Embeddings fehlen, können sie erzeugt und zurückgeschrieben werden (nicht Teil der ersten Iteration).

## Matching-Algorithmus

### Phase 1: Property Pairing
1. Extract required property containers aus dem CfP (`CapabilityMatchmakingNode.ExtractProperties`).
2. Lade offered property containers aus Neo4j (`Neo4jCapabilityPropertyQuery`).
3. Equality-first Matching:
   - Match nach `idShort` (und optional `semanticId`).
4. Embedding-Fallback:
   - Cosine Similarity zwischen required.embedding und offered.embedding.
   - Best-Score gewinnt, wenn `score >= SimilarityThreshold`.

### Phase 2: Value Validation
Für jedes gematchte Paar wird die Kompatibilität geprüft:
- Wildcard: `*` in required oder offered -> immer OK.
- Value vs Range: required.value muss innerhalb offered.min/max liegen.
- Value vs List: required.value muss in offered.listValues enthalten sein.
- Value vs Value: Gleichheit (numerisch tolerant oder string case-insensitive).
- Range/List Kombinationen analog (wie in `CapabilityPropertyDescriptor.IsCompatible`).

### Phase 3: Ergebnis
- Success: `CapabilityMatchResult` wird im Blackboard gespeichert.
- Failure: `RefusalReason` wird gesetzt.

## Implementierungsstand (Code)
- Neo4j Client wird über `InitNeo4j` aus `config.Neo4j.*` initialisiert und als `Neo4jDriver` gespeichert.
- Offered-Properties werden über `CapabilityPropertyQuery` (Neo4j) geladen.
- Matching läuft über `PropertyMatcher` (Equality-first + Embedding-Fallback).

## Tests
- Unit Tests:
  - Parsing von `embedding` aus CSV.
  - Auswahl des besten Embedding-Matches bei mehreren Kandidaten.
  - Value-Validation (Range/List/Value/Wildcard).
- Integration Tests (optional / gated):
  - Neo4j erreichbar, Query liefert Container für ein bekanntes `(moduleId, capabilityIdShort)`.

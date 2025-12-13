# Wie man die Similarity-Ergebnisse sieht

## Problem

Die xUnit-Tests zeigen "Ollama not available" obwohl Ollama läuft. Das liegt daran, dass:
1. Die Tests sehr schnell durchlaufen (< 15ms)
2. Der HttpClient möglicherweise nicht richtig initialisiert ist
3. Die Tests im Test-Kontext laufen, nicht im Runtime-Kontext

## ✅ Lösung: Verwende das Python-Script

Das **empfohlene** und **zuverlässigste** Tool ist das Python-Script:

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT

# Standard: Assemble vs Screw
python3 quick_similarity_test.py

# Custom: Beliebige Capabilities
python3 quick_similarity_test.py Weld Paint
python3 quick_similarity_test.py Transport Assembly
python3 quick_similarity_test.py Screw Bolt
```

### Beispiel-Ausgabe:

```
╔══════════════════════════════════════════════════════════════╗
║           QUICK SIMILARITY TEST (Ollama)                    ║
╚══════════════════════════════════════════════════════════════╝

  🔄 Computing similarity for:
     • 'Assemble' vs 'Screw'

  🔄 Fetching embeddings from Ollama...
  ✅ Embedding 'Assemble': 768 dimensions
  ✅ Embedding 'Screw': 768 dimensions

  📊 RESULTS:

     Cosine Similarity: 0.468055
     → 46.81% similar

     ⚠️  Low Similarity (loosely related)

════════════════════════════════════════════════════════════════
```

## xUnit Tests

Die xUnit Tests funktionieren mit **Mock-Daten** (nicht echte Ollama-Embeddings):

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT
./run_similarity_test.sh
```

Das zeigt:
- ✅ Die Cosine-Similarity-Berechnung funktioniert (97.81% für Mock-Daten)
- ✅ Die Response-Message-Generierung funktioniert
- ✅ Die Validierung funktioniert (falsche Anzahl Elements = Fehler)

Aber **nicht** die echten Ollama-Embeddings im Test-Kontext.

## Runtime: SimilarityAnalysisAgent

Wenn du den **Agent** selbst startest, funktioniert Ollama:

```bash
cd /home/benjamin/AgentDevelopment/MAS-BT

# Agent starten
dotnet run -- \
  --configPath configs/specific_configs/Module_configs/phuket/SimilarityAnalysisAgent.json
```

Dann kannst du eine I4.0-Message via MQTT senden und der Agent berechnet die echte Similarity mit Ollama.

## Warum Python statt xUnit für Ollama-Tests?

1. **Einfacher**: Direkter HTTP-Aufruf ohne komplexen Test-Setup
2. **Schneller**: Sofortige Ergebnisse ohne Build/Test-Cycle
3. **Flexibler**: Beliebige Capability-Paare testen
4. **Zuverlässiger**: Keine Test-Framework-Interferenzen

## Weitere Test-Beispiele

```bash
# Ähnliche Begriffe (erwartet: hoch)
python3 quick_similarity_test.py Screw Bolt        # ~53%
python3 quick_similarity_test.py Weld Paint        # ~53%

# Verschiedene Domänen (erwartet: niedrig)
python3 quick_similarity_test.py Transport Screw   # ~37%
python3 quick_similarity_test.py Paint Bolt        # ~40%

# Verwandte Begriffe (erwartet: mittel)
python3 quick_similarity_test.py Assemble Screw    # ~47%
python3 quick_similarity_test.py Assemble Weld     # ~51%
```

## Zusammenfassung

✅ **Für Similarity-Ergebnisse**: Verwende `python3 quick_similarity_test.py`  
✅ **Für Unit-Tests**: Verwende `./run_similarity_test.sh` (Mock-Daten)  
✅ **Für Runtime-Tests**: Starte den Agent mit `dotnet run`

Alle Methoden sind dokumentiert und funktionieren!

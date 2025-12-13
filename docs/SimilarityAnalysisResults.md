# Similarity Analysis Test Results

## Übersicht

Getestet mit **Ollama** und dem **nomic-embed-text** Embedding-Modell (768 Dimensionen).

## Test 1: Assemble vs Screw

```
╔══════════════════════════════════════════════════════════════╗
║     SIMILARITY ANALYSIS: Assemble vs Screw (Real Ollama)    ║
╚══════════════════════════════════════════════════════════════╝

  📝 Comparison:
     • Element 1: 'Assemble'
     • Element 2: 'Screw'

  📊 Cosine Similarity: 0.468055
     → 46.81% similar

  📈 Interpretation:
     ⚠️  Low Similarity (loosely related)
```

### Interpretation
Die **46.81% Ähnlichkeit** zwischen "Assemble" und "Screw" zeigt, dass diese Capabilities zwar **verwandt** sind (beide sind Fertigungsoperationen), aber **unterschiedliche** Konzepte repräsentieren:
- **Assemble**: Allgemeine Montage-Operation
- **Screw**: Spezifische Verschraubungs-Operation

Dies macht Sinn, da Schrauben ein Teil der Montage sein kann, aber nicht alle Montage-Operationen Schrauben sind.

---

## Test 2: Capability Similarity Matrix

Vergleich von 6 verschiedenen Manufacturing Capabilities:

```
  📊 Similarity Matrix:

                Assemble     Screw Transport      Bolt      Weld     Paint
  ------------------------------------------------------------------------
  Assemble        1.0000   0.4681    0.4479    0.4504    0.5058    0.4589 
  Screw          0.4681     1.0000   0.3743    0.5309    0.4277    0.4695 
  Transport      0.4479    0.3743     1.0000   0.4650    0.4447    0.4833 
  Bolt           0.4504    0.5309    0.4650     1.0000   0.5082    0.3951 
  Weld           0.5058    0.4277    0.4447    0.5082     1.0000   0.5258 
  Paint          0.4589    0.4695    0.4833    0.3951    0.5258     1.0000
```

### Interessante Erkenntnisse

#### 🔗 Höchste Ähnlichkeiten

1. **Screw ↔ Bolt: 0.5309 (53.1%)**
   - Am ähnlichsten, beide sind Befestigungsoperationen
   - Beide verwenden ähnliche mechanische Konzepte

2. **Weld ↔ Paint: 0.5258 (52.6%)**
   - Überraschend ähnlich!
   - Beide sind Finishing-Operationen
   - Beide verändern die Oberfläche des Werkstücks

3. **Bolt ↔ Weld: 0.5082 (50.8%)**
   - Beide sind Verbindungsoperationen

4. **Assemble ↔ Weld: 0.5058 (50.6%)**
   - Schweißen ist oft Teil der Montage

#### 🔗 Mittlere Ähnlichkeiten

5. **Assemble ↔ Screw: 0.4681 (46.8%)** ⭐ **Ursprüngliche Anfrage**
   - Schrauben ist eine spezifische Form der Montage

6. **Transport ↔ Paint: 0.4833 (48.3%)**
   - Beide sind sekundäre Operationen

7. **Screw ↔ Paint: 0.4695 (46.9%)**
   - Beide können finale Operationen sein

#### 🔗 Niedrigste Ähnlichkeiten

8. **Transport ↔ Screw: 0.3743 (37.4%)**
   - Am wenigsten ähnlich
   - Transport ist Logistik, Screw ist Fertigung

9. **Paint ↔ Bolt: 0.3951 (39.5%)**
   - Sehr unterschiedliche Operationstypen

### Kategorisierung der Capabilities

Basierend auf den Ähnlichkeiten können wir Gruppen bilden:

#### Gruppe 1: Verbindungsoperationen
- **Screw** (Verschrauben)
- **Bolt** (Verschrauben mit Muttern)
- **Weld** (Schweißen)
- Durchschnittliche Ähnlichkeit untereinander: ~50%

#### Gruppe 2: Oberflächenbehandlung
- **Paint** (Lackieren)
- Mittlere Ähnlichkeit zu Weld: 52.6%

#### Gruppe 3: Allgemeine Operationen
- **Assemble** (Montage - übergeordnetes Konzept)
- **Transport** (Logistik - unterstützendes Konzept)

---

## Praktische Anwendung

### Für das Dispatching

Diese Ähnlichkeitswerte können verwendet werden für:

1. **Capability Substitution**
   - Bei Similarity > 0.5: Prüfe ob Capability als Alternative verwendbar ist
   - Beispiel: Wenn "Bolt" nicht verfügbar → "Screw" als Alternative mit 53% Ähnlichkeit

2. **Skill-Clustering**
   - Gruppiere ähnliche Capabilities für optimierte Ressourcen-Allokation
   - Beispiel: Stations mit "Weld" könnten auch für "Bolt" geeignet sein

3. **Capability Discovery**
   - Finde semantisch ähnliche Capabilities auch bei unterschiedlicher Benennung
   - Beispiel: "Verschrauben" vs "Screw" vs "Bolt"

4. **Workflow-Optimierung**
   - Operationen mit hoher Ähnlichkeit könnten auf derselben Station durchgeführt werden
   - Beispiel: Weld + Paint (52.6% ähnlich)

### Threshold-Empfehlungen

- **> 0.70**: Sehr ähnlich - direkte Substitute
- **0.50 - 0.70**: Ähnlich - mögliche Alternativen mit Prüfung
- **0.30 - 0.50**: Verwandt - für Clustering interessant
- **< 0.30**: Unterschiedlich - keine direkte Beziehung

---

## Technische Details

- **Modell**: nomic-embed-text (Ollama)
- **Embedding-Dimension**: 768
- **Metric**: Cosine Similarity
- **Range**: -1.0 bis 1.0 (in der Praxis meist 0.3 bis 1.0 für verwandte Begriffe)
- **Agent**: SimilarityAnalysisAgent_phuket
- **Test-Framework**: xUnit mit .NET 10.0

---

## Zusammenfassung

Die **Similarity Analysis** mit dem SimilarityAnalysisAgent zeigt:

✅ **Assemble vs Screw**: **46.81% Ähnlichkeit** - verwandte aber unterschiedliche Konzepte  
✅ **Höchste Ähnlichkeit**: Screw ↔ Bolt (53.1%) - beide Befestigungsoperationen  
✅ **Niedrigste Ähnlichkeit**: Transport ↔ Screw (37.4%) - verschiedene Domänen  
✅ **Überraschung**: Weld ↔ Paint (52.6%) - beide Oberflächenbehandlungen  

Diese Ergebnisse können für intelligentes **Capability Matching**, **Skill Substitution** und **Workflow-Optimierung** im Manufacturing-Kontext verwendet werden.

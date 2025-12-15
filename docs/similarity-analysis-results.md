# Similarity Analysis Results

## Overview

This document describes the result formats produced by the SimilarityAnalysisAgent, including score ranges, payload structure, and recommended interpretation for downstream components.

## Result format

- `score` — numerical similarity value (0–100 or 0.0–1.0, depending on configuration).
- `items` — the compared elements and their metadata.
- `explanations` — optional textual hints or embedding distances.

## Interpretation

- Use `MinSimilarityThreshold` to filter candidates.
- Consider top-k ranking and distance normalization when combining signals.

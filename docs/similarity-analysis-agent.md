# SimilarityAnalysisAgent

## Overview

The SimilarityAnalysisAgent calculates semantic similarity between descriptions or interaction elements. This file documents configuration, capabilities, and operational details.

## Configuration

- `MaxInteractionElements`: maximum elements to consider.
- `MinSimilarityThreshold`: threshold for considering matches.
- `Ollama.Endpoint` / `Model`: embedding model configuration.

## Capabilities

- `CalcSimilarity`, `CalcPairwiseSimilarity`, `CalcDescribedSimilarity`, `CreateDescription`.

## Usage

- Subscribe to calculation topics and publish results to the dispatcher/planning components.

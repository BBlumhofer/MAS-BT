# Similarity Analysis Agent — Message Flow

## Overview

This document describes the message exchange and sequence diagrams used by the SimilarityAnalysisAgent. It covers incoming topics, outgoing notifications, and example payloads used during similarity calculations.

## Topics & Flows

- `CalcSimilarity`, `CalcPairwiseSimilarity`, `CalcDescribedSimilarity` — request topics for similarity calculations.
- `CreateDescription` — used to generate textual descriptions for items before embedding.
- Response topics and error handling patterns.

## Examples

Examples and diagrams illustrate how requests are routed and how results are published to dispatcher or planning components.

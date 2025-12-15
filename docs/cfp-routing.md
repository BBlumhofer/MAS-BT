# CFP Routing

## Overview

This document explains the Call-for-Proposal (CFP) routing logic used by the dispatching components. It covers routing strategies, priority handling, and integration with capability matching.

## Topics

- CFP lifecycle: creation, broadcast, collection of offers, selection.
- Routing patterns: broadcast and targeted CFPs using capability matching.
- Failure handling and retry strategies.

## Recommendations

- Combine capability matching with heuristics (availability, cost) for selection.
- Log CFP lifecycle events for observability and debugging.

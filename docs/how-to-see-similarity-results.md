# How to See Similarity Results

## Steps

1. Trigger a similarity calculation via the appropriate MQTT topic (e.g. `CalcSimilarity`).
2. Observe the result topic where the SimilarityAnalysisAgent publishes responses.
3. Inspect the `score` and `items` fields to understand matches and their metadata.

## Tips

- Enable INFO logging for the agent to see detailed processing logs over MQTT.
- Use the provided sample requests in the examples folder to produce reproducible results.

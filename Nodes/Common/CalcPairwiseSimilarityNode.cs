using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Common;

public class CalcPairwiseSimilarityNode : BTNode
{
    public CalcPairwiseSimilarityNode() : base("CalcPairwiseSimilarity") { }

    public override Task<NodeStatus> Execute()
    {
        try
        {
            var embeddings = Context.Get<List<double[]>>("Embeddings");
            var names = Context.Get<List<string>>("CapabilityNames");
            if (embeddings == null || embeddings.Count < 2)
            {
                Logger.LogError("CalcPairwiseSimilarity: Need at least 2 embeddings, got {Count}", embeddings?.Count ?? 0);
                return Task.FromResult(NodeStatus.Failure);
            }

            if (names == null || names.Count != embeddings.Count)
            {
                // best-effort: build default names
                names = Enumerable.Range(0, embeddings.Count).Select(i => $"Capability_{i}").ToList();
            }

            var results = new List<(int I, int J, string A, string B, double Similarity)>();

            for (int i = 0; i < embeddings.Count; i++)
            {
                for (int j = i + 1; j < embeddings.Count; j++)
                {
                    var a = embeddings[i];
                    var b = embeddings[j];
                    if (a.Length != b.Length)
                    {
                        Logger.LogWarning("CalcPairwiseSimilarity: Skipping pair with mismatched dims {I}/{J}", i, j);
                        continue;
                    }

                    double dot = 0.0, magA = 0.0, magB = 0.0;
                    for (int k = 0; k < a.Length; k++)
                    {
                        dot += a[k] * b[k];
                        magA += a[k] * a[k];
                        magB += b[k] * b[k];
                    }
                    magA = Math.Sqrt(magA);
                    magB = Math.Sqrt(magB);
                    double sim = 0.0;
                    if (magA > 0.0 && magB > 0.0)
                        sim = dot / (magA * magB);

                    results.Add((i, j, names[i], names[j], sim));
                }
            }

            Context.Set("PairwiseSimilarities", results);
            Logger.LogInformation("CalcPairwiseSimilarity: Calculated {Count} pairwise similarities", results.Count);
            return Task.FromResult(NodeStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CalcPairwiseSimilarity: Exception");
            return Task.FromResult(NodeStatus.Failure);
        }
    }
}

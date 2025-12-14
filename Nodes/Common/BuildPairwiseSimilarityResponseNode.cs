using Microsoft.Extensions.Logging;
using System.Linq;
using MAS_BT.Core;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Common;

/// <summary>
/// Builds an AAS-friendly response containing a `SimilarityMatrix` SubmodelElementCollection
/// with Pair_{n} entries holding CapabilityA, CapabilityB and Similarity properties.
/// </summary>
public class BuildPairwiseSimilarityResponseNode : BTNode
{
    public BuildPairwiseSimilarityResponseNode() : base("BuildPairwiseSimilarityResponse") { }

    public override Task<NodeStatus> Execute()
    {
        try
        {
            Logger.LogInformation("Pairwise Similarityy Triggered");
            var requestMessage = Context.Get<I40Message>("CalcSimilarityTargetMessage")
                              ?? Context.Get<I40Message>("ResponseTargetMessage")
                              ?? Context.Get<I40Message>("CurrentMessage");
            if (requestMessage == null)
            {
                Logger.LogError("BuildPairwiseSimilarityResponse: No request message in context");
                return Task.FromResult(NodeStatus.Failure);
            }

            var pairs = Context.Get<List<(int I, int J, string A, string B, double Similarity)>>("PairwiseSimilarities");
            if (pairs == null)
            {
                Logger.LogError("BuildPairwiseSimilarityResponse: No pairwise similarities in context");
                return Task.FromResult(NodeStatus.Failure);
            }

            // Sort pairs descending by similarity so highest-scoring pairs appear first
            var sortedPairs = pairs.OrderByDescending(p => p.Similarity).ToList();

            var agentId = Context.Get<string>("AgentId") ?? Context.AgentId ?? "SimilarityAnalysisAgent";
            var conversationId = requestMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();

            var builder = new I40MessageBuilder()
                .From(agentId, "AIAgent")
                .To(requestMessage.Frame?.Sender?.Identification?.Id ?? "unknown",
                    requestMessage.Frame?.Sender?.Role?.Name)
                .WithType("informConfirm")
                .WithConversationId(conversationId);

            var matrix = new SubmodelElementCollection("SimilarityMatrix");
            int idx = 0;
            foreach (var p in sortedPairs)
            {
                var pairCol = new SubmodelElementCollection($"Pair_{idx++}");
                pairCol.Add(new Property<string>("ElementA") { Value = new PropertyValue<string>(p.A) });
                pairCol.Add(new Property<string>("ElementB") { Value = new PropertyValue<string>(p.B) });
                pairCol.Add(new Property<double>("Similarity") { Value = new PropertyValue<double>(p.Similarity) });
                matrix.Add(pairCol);
            }

            builder.AddElement(matrix);

            var response = builder.Build();
            Context.Set("ResponseMessage", response);

            Logger.LogInformation("BuildPairwiseSimilarityResponse: Prepared response with {Count} pairs (Conv={Conv})", pairs.Count, conversationId);
            return Task.FromResult(NodeStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "BuildPairwiseSimilarityResponse: Exception");
            return Task.FromResult(NodeStatus.Failure);
        }
    }
}

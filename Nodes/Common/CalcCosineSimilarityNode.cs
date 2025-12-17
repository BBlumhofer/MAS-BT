using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Common;

public class CalcCosineSimilarityNode : BTNode
{
    public CalcCosineSimilarityNode() : base("CalcCosineSimilarity") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            // Capture the request message once to avoid correlation issues when other BT branches
            // overwrite shared blackboard keys (e.g., CurrentMessage) while we do async work.
            var requestMessage = Context.Get<I40Message>("CalcSimilarityTargetMessage")
                              ?? Context.Get<I40Message>("ResponseTargetMessage")
                              ?? Context.Get<I40Message>("CurrentMessage")
                              ?? Context.Get<I40Message>("LastReceivedMessage");

            var embeddings = Context.Get<List<double[]>>("Embeddings");
            if (embeddings == null || embeddings.Count != 2)
            {
                Logger.LogError("CalcCosineSimilarity: Expected exactly 2 embeddings, got {Count}", 
                    embeddings?.Count ?? 0);
                return NodeStatus.Failure;
            }

            var embedding1 = embeddings[0];
            var embedding2 = embeddings[1];

            if (embedding1.Length != embedding2.Length)
            {
                Logger.LogError("CalcCosineSimilarity: Embedding dimensions do not match ({Dim1} vs {Dim2})", 
                    embedding1.Length, embedding2.Length);
                return NodeStatus.Failure;
            }

            var similarity = CalculateCosineSimilarity(embedding1, embedding2);
            
            Context.Set("CosineSimilarity", similarity);
            Logger.LogInformation("CalcCosineSimilarity: Calculated cosine similarity = {Similarity:F4}", similarity);

            await BuildAndStoreResponseMessage(similarity, requestMessage);

            // Prevent stale correlation keys from leaking across requests.
            Context.Remove("CalcSimilarityTargetMessage");

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CalcCosineSimilarity: Exception occurred");
            return NodeStatus.Failure;
        }
    }

    private double CalculateCosineSimilarity(double[] vec1, double[] vec2)
    {
        double dotProduct = 0.0;
        double magnitude1 = 0.0;
        double magnitude2 = 0.0;

        for (int i = 0; i < vec1.Length; i++)
        {
            dotProduct += vec1[i] * vec2[i];
            magnitude1 += vec1[i] * vec1[i];
            magnitude2 += vec2[i] * vec2[i];
        }

        magnitude1 = Math.Sqrt(magnitude1);
        magnitude2 = Math.Sqrt(magnitude2);

        if (magnitude1 == 0.0 || magnitude2 == 0.0)
        {
            Logger.LogWarning("CalcCosineSimilarity: Zero magnitude vector detected");
            return 0.0;
        }

        return dotProduct / (magnitude1 * magnitude2);
    }

    private async Task BuildAndStoreResponseMessage(double similarity, I40Message? requestMessage)
    {
        if (requestMessage == null)
        {
            Logger.LogWarning("CalcCosineSimilarity: CurrentMessage not found for response building");
            return;
        }

        var agentId = Context.Get<string>("AgentId") ?? "SimilarityAnalysisAgent";
        var conversationId = requestMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();

        var builder = new I40MessageBuilder()
            .From(agentId, "AIAgent")
            .To(requestMessage.Frame?.Sender?.Identification?.Id ?? "unknown", 
                requestMessage.Frame?.Sender?.Role?.Name)
            .WithType("informConfirm")
            .WithConversationId(conversationId);

        var similarityProperty = new Property<double>("CosineSimilarity")
        {
            Value = new PropertyValue<double>(similarity)
        };

        builder.AddElement(similarityProperty);

        var responseMessage = builder.Build();

        Context.Set("ResponseMessage", responseMessage);
        Logger.LogInformation("CalcCosineSimilarity: Response message prepared with similarity {Similarity:F4}", 
            similarity);

        await Task.CompletedTask;
    }
}

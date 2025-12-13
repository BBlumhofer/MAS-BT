using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Common;

/// <summary>
/// Builds a response message for the CalcDescribedSimilarity flow.
/// The response contains three properties: Description1_Result, Description2_Result, CosineSimilarity.
/// </summary>
public class BuildDescribedSimilarityResponseNode : BTNode
{
    public BuildDescribedSimilarityResponseNode() : base("BuildDescribedSimilarityResponse") { }

    public override Task<NodeStatus> Execute()
    {
        try
        {
            var requestMessage = Context.Get<I40Message>("ResponseTargetMessage")
                              ?? Context.Get<I40Message>("CurrentMessage");
            if (requestMessage == null)
            {
                Logger.LogError("BuildDescribedSimilarityResponse: No request message in context");
                return Task.FromResult(NodeStatus.Failure);
            }

            var desc1 = Context.Get<string>("Description1_Result") ?? string.Empty;
            var desc2 = Context.Get<string>("Description2_Result") ?? string.Empty;

            var similarity = 0.0;
            try
            {
                similarity = Context.Get<double>("CosineSimilarity");
            }
            catch
            {
                // best-effort; keep 0.0
            }

            var agentId = Context.Get<string>("AgentId") ?? Context.AgentId ?? "SimilarityAnalysisAgent";
            var conversationId = requestMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();

            var builder = new I40MessageBuilder()
                .From(agentId, "AIAgent")
                .To(requestMessage.Frame?.Sender?.Identification?.Id ?? "unknown",
                    requestMessage.Frame?.Sender?.Role?.Name)
                .WithType("informConfirm")
                .WithConversationId(conversationId);

            builder.AddElement(new Property<string>("Description1_Result") { Value = new PropertyValue<string>(desc1) });
            builder.AddElement(new Property<string>("Description2_Result") { Value = new PropertyValue<string>(desc2) });
            builder.AddElement(new Property<double>("CosineSimilarity") { Value = new PropertyValue<double>(similarity) });

            var responseMessage = builder.Build();
            Context.Set("ResponseMessage", responseMessage);

            Logger.LogInformation(
                "BuildDescribedSimilarityResponse: Prepared response (Conv={Conv}, Similarity={Similarity:F4})",
                conversationId,
                similarity);

            return Task.FromResult(NodeStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "BuildDescribedSimilarityResponse: Exception occurred");
            return Task.FromResult(NodeStatus.Failure);
        }
    }
}

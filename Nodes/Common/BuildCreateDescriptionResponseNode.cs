using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Common;

/// <summary>
/// Builds a response for CreateDescription with one property `Description_Result`.
/// </summary>
public class BuildCreateDescriptionResponseNode : BTNode
{
    public BuildCreateDescriptionResponseNode() : base("BuildCreateDescriptionResponse") { }

    public override Task<NodeStatus> Execute()
    {
        try
        {
            var requestMessage = Context.Get<I40Sharp.Messaging.Models.I40Message>("CreateDescriptionTargetMessage")
                              ?? Context.Get<I40Sharp.Messaging.Models.I40Message>("CurrentMessage");
            if (requestMessage == null)
            {
                Logger.LogError("BuildCreateDescriptionResponse: No request message in context");
                return Task.FromResult(NodeStatus.Failure);
            }

            var desc = Context.Get<string>("Description_Result") ?? string.Empty;

            var agentId = Context.Get<string>("AgentId") ?? Context.AgentId ?? "SimilarityAnalysisAgent";
            var conversationId = requestMessage.Frame?.ConversationId ?? Guid.NewGuid().ToString();

            var builder = new I40MessageBuilder()
                .From(agentId, "AIAgent")
                .To(requestMessage.Frame?.Sender?.Identification?.Id ?? "unknown",
                    requestMessage.Frame?.Sender?.Role?.Name)
                .WithType("informConfirm")
                .WithConversationId(conversationId);

            builder.AddElement(new Property<string>("Description_Result") { Value = new PropertyValue<string>(desc) });

            var responseMessage = builder.Build();
            Context.Set("ResponseMessage", responseMessage);

            Logger.LogInformation("BuildCreateDescriptionResponse: Prepared response (Conv={Conv})", conversationId);
            return Task.FromResult(NodeStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "BuildCreateDescriptionResponse: Exception occurred");
            return Task.FromResult(NodeStatus.Failure);
        }
    }
}

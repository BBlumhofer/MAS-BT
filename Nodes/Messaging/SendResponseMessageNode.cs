using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Sends a pre-built response message from the context back to the sender
/// </summary>
public class SendResponseMessageNode : BTNode
{
    public string? Topic { get; set; }
    
    public SendResponseMessageNode() : base("SendResponseMessage")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("SendResponseMessage: MessagingClient not available or disconnected");
                return NodeStatus.Failure;
            }

            var responseMessage = Context.Get<I40Message>("ResponseMessage");
            if (responseMessage == null)
            {
                Logger.LogError("SendResponseMessage: No ResponseMessage found in context");
                return NodeStatus.Failure;
            }

            // Determine topic: use explicit Topic parameter, or derive from request
            string topicToUse;
            if (!string.IsNullOrWhiteSpace(Topic))
            {
                topicToUse = ResolvePlaceholders(Topic);
            }
            else
            {
                // Send back to the sender's topic (from the request)
                var requestMessage = Context.Get<I40Message>("CurrentMessage");
                if (requestMessage?.Frame?.Sender?.Identification?.Id != null)
                {
                    var ns = Context.Get<string>("Namespace") ?? "phuket";
                    var senderTopic = requestMessage.Frame.Type; // Use message type as topic suffix
                    topicToUse = $"/{ns}/{requestMessage.Frame.Sender.Identification.Id}/{senderTopic}";
                }
                else
                {
                    topicToUse = $"/{Context.Get<string>("Namespace") ?? "phuket"}/responses";
                }
            }

            await client.PublishAsync(responseMessage, topicToUse);

            Logger.LogInformation("SendResponseMessage: Published response (Type={Type}, Conv={Conv}) to topic {Topic}", 
                responseMessage.Frame?.Type, 
                responseMessage.Frame?.ConversationId, 
                topicToUse);

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendResponseMessage: Error sending response message");
            return NodeStatus.Failure;
        }
    }
}

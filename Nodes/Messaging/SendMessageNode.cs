using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// SendMessage - Sends a generic message to another agent or broadcast
/// </summary>
public class SendMessageNode : BTNode
{
    public string AgentId { get; set; } = string.Empty;
    public object? Payload { get; set; }
    public string MessageType { get; set; } = "StatusUpdate";
    public string? ConversationId { get; set; }
    public bool WaitForReply { get; set; } = false;
    public int ReplyTimeoutSeconds { get; set; } = 30;
    public string? Topic { get; set; }
    
    public SendMessageNode() : base("SendMessage")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("SendMessage: Sending {MessageType} to {AgentId}", MessageType, AgentId);
        
        try
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client == null || !client.IsConnected)
            {
                Logger.LogError("SendMessage: MessagingClient not available or disconnected");
                return NodeStatus.Failure;
            }

            // Determine conversationId: reuse provided or create a new conversation
            var convId = !string.IsNullOrWhiteSpace(ConversationId)
                ? ConversationId!
                : client.CreateConversation(TimeSpan.FromSeconds(ReplyTimeoutSeconds));

            // Build I40 message
            var agentRole = string.IsNullOrWhiteSpace(Context.AgentRole) ? "ExecutionAgent" : Context.AgentRole;
            var builder = new I40MessageBuilder()
                .From(Context.AgentId, agentRole)
                .To(AgentId, null)
                .WithType(MessageType)
                .WithConversationId(convId);

            // Attach payload if it's a SubmodelElement (AAS) or generic object
            if (Payload is BaSyx.Models.AdminShell.SubmodelElement sme)
            {
                builder.AddElement(sme);
            }
            else if (Payload != null)
            {
                try
                {
                    // attempt to serialize object into a SubmodelElement using existing helpers
                    // fallback: add as a simple Property<string>
                    var json = System.Text.Json.JsonSerializer.Serialize(Payload);
                    var prop = new BaSyx.Models.AdminShell.Property<string>("Payload") { Value = new BaSyx.Models.AdminShell.PropertyValue<string>(json) };
                    builder.AddElement(prop);
                }
                catch
                {
                    var prop = new BaSyx.Models.AdminShell.Property<string>("Payload") { Value = new BaSyx.Models.AdminShell.PropertyValue<string>(Payload.ToString() ?? string.Empty) };
                    builder.AddElement(prop);
                }
            }

            var message = builder.Build();

            var topicToUse = Topic ?? $"/Modules/{Context.Get<string>("ModuleId") ?? Context.AgentId}/{MessageType}/";

            await client.PublishAsync(message, topicToUse);

            Context.Set("sent", true);
            Context.Set("lastConversationId", convId);

            Logger.LogInformation("SendMessage: Published {MessageType} to {AgentId} (conversation={Conversation}) on topic {Topic}", MessageType, AgentId, convId, topicToUse);

            if (WaitForReply)
            {
                var tcs = new TaskCompletionSource<I40Sharp.Messaging.Models.I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Callback(I40Sharp.Messaging.Models.I40Message m)
                {
                    try { tcs.TrySetResult(m); } catch { }
                }

                client.OnConversation(convId, Callback);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(ReplyTimeoutSeconds)));
                if (completed == tcs.Task)
                {
                    var reply = await tcs.Task;
                    Context.Set("LastReceivedMessage", reply);
                    Logger.LogInformation("SendMessage: Received reply for conversation {Conv}", convId);
                    return NodeStatus.Success;
                }
                else
                {
                    Logger.LogWarning("SendMessage: Timeout waiting for reply (conversation={Conv})", convId);
                    return NodeStatus.Failure;
                }
            }

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendMessage: Error sending message to {AgentId}", AgentId);
            return NodeStatus.Failure;
        }
    }
}

using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using AasSharpClient.Models.Messages;
using UAClient.Client;

namespace MAS_BT.Services
{
    // Subscriber that publishes MQTT log/error messages when RemoteServer connection changes
    public class RemoteServerMqttNotifier : IRemoteServerSubscriber
    {
        private readonly BTContext _context;

        public RemoteServerMqttNotifier(BTContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void OnConnectionLost()
        {
            _ = Task.Run(async () => await PublishLogAsync(LogMessage.LogLevel.Error, "OPC UA connection lost"));
        }

        public void OnConnectionEstablished()
        {
            _ = Task.Run(async () => await PublishLogAsync(LogMessage.LogLevel.Info, "OPC UA connection established"));
        }

        public void OnServerTimeUpdate(DateTime time)
        {
            // no-op
        }

        public void OnStatusChange(RemoteServerStatus status)
        {
            // optional: publish status changes at debug level
            _ = Task.Run(async () => await PublishLogAsync(LogMessage.LogLevel.Debug, $"RemoteServer status: {status}"));
        }

        private async Task PublishLogAsync(LogMessage.LogLevel level, string message)
        {
            try
            {
                var messagingClient = _context.Get<MessagingClient>("MessagingClient");
                if (messagingClient == null || !messagingClient.IsConnected) return;

                var agentId = _context.AgentId ?? "UnknownAgent";
                var agentRole = _context.AgentRole ?? "ResourceHolon";

                var logElement = new LogMessage(level, message, agentRole, agentId);
                var builder = new I40MessageBuilder()
                    .From(agentId, agentRole)
                    .To("broadcast", string.Empty)
                    .WithType(I40MessageTypes.INFORM)
                    .WithConversationId(Guid.NewGuid().ToString())
                    .AddElement(logElement);

                // publish on agent-specific logs topic
                var topic = $"{agentId}/logs";
                await messagingClient.PublishAsync(builder.Build(), topic);
            }
            catch
            {
                // swallow exceptions - monitoring should not crash the tree
            }
        }
    }
}

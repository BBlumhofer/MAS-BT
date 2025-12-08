using System;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models.Messages;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// SendLogMessage - Sendet Log-Nachrichten via I4.0 Messaging
/// </summary>
public class SendLogMessageNode : BTNode
{
    public string LogLevel { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
    public string ModuleId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty; // Optional, default wird verwendet

    public SendLogMessageNode() : base("SendLogMessage")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        Logger.LogInformation("SendLogMessage: Sending log message '{Message}' (Level: {LogLevel})", Message, LogLevel);
        
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogWarning("SendLogMessage: MessagingClient not found in context, skipping MQTT publish");
            // Gebe trotzdem Success zurück, damit der Tree weiterläuft
            return NodeStatus.Success;
        }
        
        try
        {
            // Verwende ModuleId aus Parameter oder Context
            var moduleId = ModuleId;
            if (string.IsNullOrEmpty(moduleId))
            {
                moduleId = Context.Get<string>("ModuleId") ?? "UnknownModule";
            }

            var agentRole = string.IsNullOrWhiteSpace(Context.AgentRole) ? "ExecutionAgent" : Context.AgentRole;
            var agentState = Context.Get<string>($"ModuleState_{moduleId}") ?? string.Empty;
            var parsedLogLevel = ParseLogLevel(LogLevel);

            var logElement = new LogMessage(parsedLogLevel, Message, agentRole, agentState, moduleId);

            var logMessage = new I40MessageBuilder()
                .From($"{moduleId}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType(I40MessageTypes.INFORM)
                .AddElement(logElement)
                .Build();

            // Sende über angegebenes Topic oder Default-Topic
            var topic = !string.IsNullOrEmpty(Topic) ? Topic : $"/Modules/{moduleId}/Logs/";
            await client.PublishAsync(logMessage, topic);

            Logger.LogInformation("SendLogMessage: Sent log message to MQTT topic '{Topic}'", topic);
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendLogMessage: Failed to send log message");
            return NodeStatus.Failure;
        }
    }
    
    private static LogMessage.LogLevel ParseLogLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return LogMessage.LogLevel.Info;
        }

        if (Enum.TryParse<LogMessage.LogLevel>(level, true, out var parsed))
        {
            return parsed;
        }

        return level.Trim().ToUpperInvariant() switch
        {
            "WARNING" => LogMessage.LogLevel.Warn,
            "CRITICAL" => LogMessage.LogLevel.Fatal,
            _ => LogMessage.LogLevel.Info
        };
    }
}

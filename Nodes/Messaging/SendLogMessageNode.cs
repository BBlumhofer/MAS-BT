using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

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

            // Erstelle Properties mit korrekten Values
            var logLevelProp = new Property<string>("LogLevel");
            logLevelProp.Value = new PropertyValue<string>(LogLevel);
            
            var messageProp = new Property<string>("Message");
            messageProp.Value = new PropertyValue<string>(Message);
            
            var timestampProp = new Property<string>("Timestamp");
            timestampProp.Value = new PropertyValue<string>(DateTime.UtcNow.ToString("o"));
            
            var moduleProp = new Property<string>("ModuleId");
            moduleProp.Value = new PropertyValue<string>(moduleId);

            // Erstelle I4.0 Message mit Log-Informationen
            var logMessage = new I40MessageBuilder()
                .From($"{moduleId}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType(I40MessageTypes.INFORM)
                .AddElement(logLevelProp)
                .AddElement(messageProp)
                .AddElement(timestampProp)
                .AddElement(moduleProp)
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
    
    private static ISubmodelElement CreateProperty(string idShort, string value)
    {
        var prop = new Property<string>(idShort);
        prop.Value = new PropertyValue<string>(value);
        return prop;
    }
    
    private static string GetLogLevelName(Microsoft.Extensions.Logging.LogLevel logLevel)
    {
        return logLevel switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => "TRACE",
            Microsoft.Extensions.Logging.LogLevel.Debug => "DEBUG",
            Microsoft.Extensions.Logging.LogLevel.Information => "INFO",
            Microsoft.Extensions.Logging.LogLevel.Warning => "WARNING",
            Microsoft.Extensions.Logging.LogLevel.Error => "ERROR",
            Microsoft.Extensions.Logging.LogLevel.Critical => "CRITICAL",
            _ => "NONE"
        };
    }
}

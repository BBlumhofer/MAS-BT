using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;

namespace MAS_BT.Services;

/// <summary>
/// Logger Provider der automatisch Logs via MQTT sendet
/// </summary>
public class MqttLoggerProvider : ILoggerProvider
{
    private readonly MessagingClient? _messagingClient;
    private readonly string _agentId;
    private readonly string _agentRole;
    private readonly ConcurrentDictionary<string, MqttLogger> _loggers = new();
    
    public MqttLoggerProvider(MessagingClient? messagingClient, string agentId, string agentRole = "ResourceHolon")
    {
        _messagingClient = messagingClient;
        _agentId = agentId;
        _agentRole = agentRole;
    }
    
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => 
            new MqttLogger(name, _messagingClient, _agentId, _agentRole));
    }
    
    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Logger der automatisch Logs via MQTT publiziert
/// </summary>
public class MqttLogger : ILogger
{
    private readonly string _categoryName;
    private readonly MessagingClient? _messagingClient;
    private readonly string _agentId;
    private readonly string _agentRole;
    
    public MqttLogger(string categoryName, MessagingClient? messagingClient, string agentId, string agentRole)
    {
        _categoryName = categoryName;
        _messagingClient = messagingClient;
        _agentId = agentId;
        _agentRole = agentRole;
    }
    
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        
        var message = formatter(state, exception);
        
        // Konsolen-Output (wie bisher)
        var logLevelString = GetLogLevelString(logLevel);
        Console.WriteLine($"{logLevelString}: {_categoryName}[{eventId.Id}]");
        Console.WriteLine($"      {message}");
        
        if (exception != null)
        {
            Console.WriteLine($"      Exception: {exception}");
        }
        
        // MQTT-Output (nur ab INFO)
        if (logLevel >= LogLevel.Information && _messagingClient != null && _messagingClient.IsConnected)
        {
            _ = SendLogMessageAsync(logLevel, message);
        }
    }
    
    private async Task SendLogMessageAsync(LogLevel logLevel, string message)
    {
        try
        {
            var i40Message = CreateI40LogMessage(logLevel, message);
            var topic = $"{_agentId}/logs";
            await _messagingClient!.PublishAsync(i40Message, topic);
        }
        catch (Exception ex)
        {
            // Fehler beim MQTT-Senden nicht eskalieren
            Console.WriteLine($"warn: Failed to send log via MQTT: {ex.Message}");
        }
    }
    
    private I40Message CreateI40LogMessage(LogLevel logLevel, string message)
    {
        var logLevelProp = CreateProperty("LogLevel", GetLogLevelName(logLevel));
        var messageProp = CreateProperty("Message", message);
        var timestampProp = CreateProperty("Timestamp", DateTime.UtcNow.ToString("o"));
        var agentRoleProp = CreateProperty("AgentRole", _agentRole);

        return new I40Message
        {
            Frame = new MessageFrame
            {
                Sender = new Participant
                {
                    Identification = new Identification { Id = _agentId },
                    Role = new Role { Name = _agentRole }
                },
                Receiver = new Participant
                {
                    Identification = new Identification { Id = "broadcast" },
                    Role = new Role { Name = "" }
                },
                Type = "inform",
                ConversationId = Guid.NewGuid().ToString(),
                MessageId = Guid.NewGuid().ToString()
            },
            InteractionElements = new List<ISubmodelElement>
            {
                logLevelProp,
                messageProp,
                timestampProp,
                agentRoleProp
            }.Cast<SubmodelElement>().ToList()
        };
    }
    
    private static ISubmodelElement CreateProperty(string idShort, string value)
    {
        var prop = new Property<string>(idShort);
        prop.Value = new PropertyValue<string>(value);
        return prop;
    }
    
    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };
    }
    
    private static string GetLogLevelName(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => "NONE"
        };
    }
}

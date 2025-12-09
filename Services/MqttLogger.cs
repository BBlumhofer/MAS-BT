using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models.Messages;
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
    
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
    
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
        
        // MQTT-Output (Debug and above)
        if (logLevel >= LogLevel.Debug && _messagingClient != null && _messagingClient.IsConnected)
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
        var logCollection = new LogMessage(
            GetLogLevelName(logLevel),
            message,
            _agentRole,
            _agentId);

        var builder = new I40MessageBuilder()
            .From(_agentId, _agentRole)
            .To("broadcast", string.Empty)
            .WithType(I40MessageTypes.INFORM)
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(logCollection);

        return builder.Build();
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

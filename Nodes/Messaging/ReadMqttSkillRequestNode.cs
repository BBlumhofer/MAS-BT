using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using System.Collections.Concurrent;
using System.Text.Json;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// ReadMqttSkillRequest - Liest Action von Planning Agent via MQTT
/// Topic: /Modules/{ModuleID}/SkillRequest/
/// </summary>
public class ReadMqttSkillRequestNode : BTNode
{
    public string ModuleId { get; set; } = "";
    public int TimeoutMs { get; set; } = 100; // Non-blocking polling
    
    private readonly ConcurrentQueue<string> _messageQueue = new(); // Store raw JSON
    private bool _subscribed = false;
    
    public ReadMqttSkillRequestNode() : base("ReadMqttSkillRequest")
    {
    }
    
    public ReadMqttSkillRequestNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("ReadMqttSkillRequest: Checking for SkillRequest on module '{ModuleId}'", ModuleId);
        
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("ReadMqttSkillRequest: MessagingClient not found in context");
            return NodeStatus.Failure;
        }
        
        // Subscribe zum SkillRequest Topic (falls noch nicht geschehen)
        if (!_subscribed)
        {
            var topic = $"/Modules/{ModuleId}/SkillRequest/";
            
            try
            {
                await client.SubscribeAsync(topic);
                
                // Registriere Callback für RAW JSON (umgehe BaSyx Deserialization Problem)
                var transport = client.GetType().GetField("_transport", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                    .GetValue(client);
                
                if (transport != null)
                {
                    var messageReceivedEvent = transport.GetType().GetEvent("MessageReceived");
                    if (messageReceivedEvent != null)
                    {
                        EventHandler<I40Sharp.Messaging.Transport.MessageReceivedEventArgs>? handler = null;
                        handler = (sender, e) =>
                        {
                            if (e.Topic == topic)
                            {
                                _messageQueue.Enqueue(e.Payload);
                                Logger.LogInformation("ReadMqttSkillRequest: Received raw SkillRequest message");
                            }
                        };
                        messageReceivedEvent.AddEventHandler(transport, handler);
                    }
                }
                
                _subscribed = true;
                Logger.LogInformation("ReadMqttSkillRequest: Subscribed to topic '{Topic}'", topic);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ReadMqttSkillRequest: Failed to subscribe to topic '{Topic}'", topic);
                return NodeStatus.Failure;
            }
        }
        
        // Prüfe ob Message in Queue (non-blocking)
        if (_messageQueue.TryDequeue(out var jsonPayload))
        {
            try
            {
                // Parse RAW JSON direkt ohne BaSyx SDK
                using var doc = JsonDocument.Parse(jsonPayload);
                var root = doc.RootElement;
                
                // Extrahiere Frame
                if (!root.TryGetProperty("frame", out var frameElement))
                {
                    Logger.LogWarning("ReadMqttSkillRequest: No 'frame' found in message");
                    return NodeStatus.Failure;
                }
                
                var conversationId = frameElement.GetProperty("conversationId").GetString() ?? "";
                var messageId = frameElement.GetProperty("messageId").GetString() ?? "";
                var senderId = frameElement.GetProperty("sender").GetProperty("identification").GetProperty("id").GetString() ?? "";
                
                // Extrahiere InteractionElements
                if (!root.TryGetProperty("interactionElements", out var interactionElements))
                {
                    Logger.LogWarning("ReadMqttSkillRequest: No 'interactionElements' found in message");
                    return NodeStatus.Failure;
                }
                
                // Finde Action (erstes SubmodelElementCollection mit idShort="Action*")
                JsonElement? actionElement = null;
                foreach (var element in interactionElements.EnumerateArray())
                {
                    if (element.TryGetProperty("idShort", out var idShort) && 
                        idShort.GetString()?.StartsWith("Action") == true)
                    {
                        actionElement = element;
                        break;
                    }
                }
                
                if (actionElement == null)
                {
                    Logger.LogWarning("ReadMqttSkillRequest: No Action found in interactionElements");
                    return NodeStatus.Failure;
                }
                
                // Extrahiere Action Properties aus value[] Array
                var actionValue = actionElement.Value.GetProperty("value");
                var actionTitle = ExtractPropertyValue(actionValue, "ActionTitle");
                var status = ExtractPropertyValue(actionValue, "Status");
                var machineName = ExtractPropertyValue(actionValue, "MachineName");
                
                Logger.LogInformation("ReadMqttSkillRequest: Received Action '{ActionTitle}' with status '{Status}' for machine '{MachineName}'", 
                    actionTitle, status, machineName);
                
                // Speichere im Context
                Context.Set("ActionTitle", actionTitle);
                Context.Set("ActionStatus", status);
                Context.Set("MachineName", machineName);
                Context.Set("ConversationId", conversationId);
                Context.Set("OriginalMessageId", messageId);
                Context.Set("RequestSender", senderId);
                
                // Extrahiere InputParameters
                var inputParams = new Dictionary<string, string>();
                foreach (var prop in actionValue.EnumerateArray())
                {
                    if (prop.TryGetProperty("idShort", out var propIdShort) && 
                        propIdShort.GetString() == "InputParameters")
                    {
                        if (prop.TryGetProperty("value", out var paramsArray))
                        {
                            foreach (var param in paramsArray.EnumerateArray())
                            {
                                if (param.TryGetProperty("idShort", out var paramName) &&
                                    param.TryGetProperty("value", out var paramValue))
                                {
                                    inputParams[paramName.GetString() ?? ""] = paramValue.GetString() ?? "";
                                }
                            }
                        }
                        break;
                    }
                }
                
                Context.Set("InputParameters", inputParams);
                Logger.LogInformation("ReadMqttSkillRequest: Extracted {Count} input parameters", inputParams.Count);
                
                return NodeStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ReadMqttSkillRequest: Failed to parse Action from JSON");
                return NodeStatus.Failure;
            }
        }
        
        // Keine Message verfügbar - Running State (wartet weiter)
        return NodeStatus.Running;
    }
    
    private string ExtractPropertyValue(JsonElement valueArray, string propertyName)
    {
        foreach (var element in valueArray.EnumerateArray())
        {
            if (element.TryGetProperty("idShort", out var idShort) && 
                idShort.GetString() == propertyName)
            {
                if (element.TryGetProperty("value", out var value))
                {
                    return value.GetString() ?? "";
                }
            }
        }
        return "";
    }
    
    public override async Task OnAbort()
    {
        // Unsubscribe when aborted
        if (_subscribed)
        {
            var client = Context.Get<MessagingClient>("MessagingClient");
            if (client != null)
            {
                try
                {
                    var topic = $"/Modules/{ModuleId}/SkillRequest/";
                    await client.UnsubscribeAsync(topic);
                    Logger.LogInformation("ReadMqttSkillRequest: Unsubscribed from topic");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "ReadMqttSkillRequest: Failed to unsubscribe");
                }
            }
            _subscribed = false;
        }
        _messageQueue.Clear();
    }
    
    public override Task OnReset()
    {
        _messageQueue.Clear();
        return Task.CompletedTask;
    }
}

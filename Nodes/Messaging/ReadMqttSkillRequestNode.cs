using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using System.Collections.Concurrent;
using System.Text.Json;
using AasSharpClient.Models;

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
                var inputParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in actionValue.EnumerateArray())
                {
                    if (prop.TryGetProperty("idShort", out var propIdShort) && 
                        propIdShort.GetString() == "InputParameters")
                    {
                        if (prop.TryGetProperty("value", out var paramsArray))
                        {
                            foreach (var param in paramsArray.EnumerateArray())
                            {
                                if (param.TryGetProperty("idShort", out var paramName))
                                {
                                    // Hole valueType falls vorhanden (z.B. "xs:boolean", "xs:integer")
                                    string? valueType = null;
                                    if (param.TryGetProperty("valueType", out var valueTypeElement))
                                    {
                                        valueType = valueTypeElement.GetString();
                                    }
                                    
                                    if (param.TryGetProperty("value", out var paramValue))
                                    {
                                        var typedValue = ExtractTypedValue(paramValue, valueType);
                                        inputParams[paramName.GetString() ?? ""] = typedValue;
                                    }
                                }
                            }
                        }
                        break;
                    }
                }
                
                Context.Set("InputParameters", inputParams);
                Logger.LogInformation("ReadMqttSkillRequest: Extracted {Count} input parameters", inputParams.Count);

                var actionIdShort = actionElement.Value.GetProperty("idShort").GetString() ?? "Action001";
                var aasAction = BuildAasAction(actionIdShort, actionTitle, status, machineName, inputParams);
                Context.Set("CurrentAction", aasAction);
                Context.Set("ActionId", actionIdShort);
                
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
    
    /// <summary>
    /// Extrahiert einen typisierten Wert aus einem JsonElement basierend auf seinem ValueKind und optionalem XSD ValueType
    /// </summary>
    private object ExtractTypedValue(JsonElement element, string? xsdValueType = null)
    {
        // Wenn ein XSD ValueType angegeben ist, nutze diesen für die Konvertierung
        if (!string.IsNullOrEmpty(xsdValueType))
        {
            var valueStr = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            if (string.IsNullOrEmpty(valueStr)) return "";
            
            // Normalisiere XSD Type (entferne "xs:" Prefix falls vorhanden)
            var normalizedType = xsdValueType.ToLowerInvariant();
            if (normalizedType.StartsWith("xs:")) normalizedType = normalizedType.Substring(3);
            
            switch (normalizedType)
            {
                case "boolean":
                case "bool":
                    if (bool.TryParse(valueStr, out var boolValue))
                        return boolValue;
                    // Fallback für "1"/"0"
                    if (valueStr == "1") return true;
                    if (valueStr == "0") return false;
                    Logger.LogWarning("ReadMqttSkillRequest: Could not parse boolean value '{Value}', defaulting to false", valueStr);
                    return false;
                    
                case "integer":
                case "int":
                    if (int.TryParse(valueStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue))
                        return intValue;
                    Logger.LogWarning("ReadMqttSkillRequest: Could not parse integer value '{Value}', defaulting to 0", valueStr);
                    return 0;
                    
                case "long":
                    if (long.TryParse(valueStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longValue))
                        return longValue;
                    Logger.LogWarning("ReadMqttSkillRequest: Could not parse long value '{Value}', defaulting to 0", valueStr);
                    return 0L;
                    
                case "double":
                    if (double.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
                        return doubleValue;
                    Logger.LogWarning("ReadMqttSkillRequest: Could not parse double value '{Value}', defaulting to 0.0", valueStr);
                    return 0.0;
                    
                case "float":
                    if (float.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatValue))
                        return floatValue;
                    Logger.LogWarning("ReadMqttSkillRequest: Could not parse float value '{Value}', defaulting to 0.0", valueStr);
                    return 0.0f;
                    
                case "string":
                default:
                    return valueStr;
            }
        }
        
        // Fallback: Versuche JSON ValueKind zu nutzen
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var str = element.GetString() ?? "";
                // Auto-detect: versuche String als Boolean zu parsen
                if (bool.TryParse(str, out var autoBool))
                    return autoBool;
                // Auto-detect: versuche String als Number zu parsen
                if (int.TryParse(str, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var autoInt))
                    return autoInt;
                if (double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var autoDouble))
                    return autoDouble;
                return str;
                
            case JsonValueKind.Number:
                // Versuche zuerst Int32, dann Int64, dann Double
                if (element.TryGetInt32(out var intValue))
                    return intValue;
                if (element.TryGetInt64(out var longValue))
                    return longValue;
                if (element.TryGetDouble(out var doubleValue))
                    return doubleValue;
                return element.GetRawText();
                
            case JsonValueKind.True:
                return true;
                
            case JsonValueKind.False:
                return false;
                
            case JsonValueKind.Null:
                return "";
                
            default:
                // Für Arrays, Objects, etc. - gebe String-Repräsentation zurück
                return element.GetRawText();
        }
    }

    private static AasSharpClient.Models.Action BuildAasAction(
        string actionId,
        string actionTitle,
        string status,
        string machineName,
        IDictionary<string, object> inputParameters)
    {
        var mappedStatus = MapActionStatus(status);
        var inputModel = InputParameters.FromTypedValues(inputParameters);
        var finalResult = new FinalResultData();
        return new AasSharpClient.Models.Action(actionId, actionTitle, mappedStatus, inputModel, finalResult, null, machineName);
    }

    private static ActionStatusEnum MapActionStatus(string status)
    {
        return status?.ToLowerInvariant() switch
        {
            "planned" => ActionStatusEnum.PLANNED,
            "executing" => ActionStatusEnum.EXECUTING,
            "suspended" => ActionStatusEnum.SUSPENDED,
            "done" => ActionStatusEnum.DONE,
            "aborted" => ActionStatusEnum.ABORTED,
            "error" => ActionStatusEnum.ERROR,
            _ => ActionStatusEnum.OPEN
        };
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

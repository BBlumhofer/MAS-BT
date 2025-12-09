using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using BaSyx.Models.AdminShell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaSyx.Models.Extensions;
using AasSharpClient.Models;
using ActionModel = AasSharpClient.Models.Action;

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

    private static readonly JsonSerializerOptions BasyxOptions = new()
    {
        Converters = { new FullSubmodelElementConverter(new ConverterOptions()), new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    
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
                
                SubmodelElementCollection? actionCollection = null;
                JsonElement? rawActionElement = null;

                foreach (var element in interactionElements.EnumerateArray())
                {
                    var raw = element.GetRawText();
                    // Log the raw JSON we attempt to parse with BaSyx for easier debugging
                    Logger.LogInformation("ReadMqttSkillRequest: Attempting BaSyx parse of interaction element JSON: {Json}", raw);
                    try
                    {
                        // BaSyx-konforme Deserialisierung mit Fallback ähnlich Testhelper
                        var collection = BuildCollectionFromElement(element);
                        if (!string.IsNullOrEmpty(collection.IdShort) && collection.IdShort.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                        {
                            actionCollection = collection;
                            rawActionElement = element;
                            break;
                        }
                        // Falls keine Collection erkannt, versuche direkten Deserialize (kann z.B. bei einzelnen SubmodelElements greifen)
                        var sme = System.Text.Json.JsonSerializer.Deserialize<ISubmodelElement>(raw, BasyxOptions);
                        if (sme is SubmodelElementCollection col && !string.IsNullOrEmpty(col.IdShort) && col.IdShort.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                        {
                            actionCollection = col;
                            rawActionElement = element;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Wenn BaSyx-Deserialisierung fehlschlägt, logge Exception + das rohe JSON und versuche weiterhin eine manuelle Extraktion
                        Logger.LogInformation("ReadMqttSkillRequest: BaSyx deserialization failed for interaction element; raw JSON: {Json}. Exception: {Ex}", raw, ex.Message);
                        if (element.TryGetProperty("idShort", out var idShort) && idShort.GetString()?.StartsWith("Action") == true)
                        {
                            rawActionElement = element;
                            break;
                        }
                    }
                }

                if (actionCollection == null && rawActionElement == null)
                {
                    Logger.LogWarning("ReadMqttSkillRequest: No Action found in interactionElements");
                    return NodeStatus.Failure;
                }

                string actionTitle = string.Empty;
                string status = string.Empty;
                string machineName = string.Empty;

                // Wenn BaSyx-Collection vorhanden, extrahiere Werte daraus, sonst nutze das rohe JsonElement
                if (actionCollection != null)
                {
                    var actionModel = CreateActionFromCollection(actionCollection);

                    actionTitle = actionModel.ActionTitle.Value.Value?.ToString() ?? string.Empty;
                    status = actionModel.State.ToString();
                    machineName = actionModel.MachineName.Value.Value?.ToString() ?? string.Empty;

                    var inputParams = ExtractInputParametersDictionary(actionModel);
                    Context.Set("InputParameters", inputParams);
                    Logger.LogInformation("ReadMqttSkillRequest: Extracted {Count} input parameters (BaSyx)", inputParams.Count);

                    Context.Set("CurrentAction", actionModel);
                    Context.Set("ActionId", actionModel.IdShort ?? "Action001");
                }
                else if (rawActionElement.HasValue)
                {
                    var actionValue = rawActionElement.Value.GetProperty("value");
                    actionTitle = ExtractPropertyValue(actionValue, "ActionTitle");
                    status = ExtractPropertyValue(actionValue, "Status");
                    machineName = ExtractPropertyValue(actionValue, "MachineName");

                    // Extrahiere InputParameters (manuelle Pfad wie vorher)
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
                }

                Logger.LogInformation("ReadMqttSkillRequest: Received Action '{ActionTitle}' with status '{Status}' for machine '{MachineName}'", 
                    actionTitle, status, machineName);
                
                // Speichere im Context
                Context.Set("ActionTitle", actionTitle);
                Context.Set("ActionStatus", status);
                Context.Set("MachineName", machineName);
                Context.Set("ConversationId", conversationId);
                Context.Set("OriginalMessageId", messageId);
                Context.Set("RequestSender", senderId);

                // InputParameters sollten bereits in Context gesetzt worden sein (BaSyx oder manuell)
                var inputParamsFromContext = Context.Get<Dictionary<string, object>>("InputParameters") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                // Bestimme actionId
                string actionIdShort;
                if (actionCollection != null)
                    actionIdShort = actionCollection.IdShort ?? "Action001";
                else if (rawActionElement.HasValue && rawActionElement.Value.TryGetProperty("idShort", out var idShortProp))
                    actionIdShort = idShortProp.GetString() ?? "Action001";
                else
                    actionIdShort = "Action001";

                var existingAction = Context.Get<ActionModel>("CurrentAction");
                if (existingAction == null)
                {
                    var aasAction = BuildAasAction(actionIdShort, actionTitle, status, machineName, inputParamsFromContext);
                    Context.Set("CurrentAction", aasAction);
                }
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

    private static SubmodelElementCollection BuildCollectionFromElement(JsonElement element)
    {
        var idShort = element.TryGetProperty("idShort", out var idShortNode)
            ? idShortNode.GetString() ?? "Collection"
            : "Collection";

        var collection = new SubmodelElementCollection(idShort);

        if (element.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in val.EnumerateArray())
            {
                var sme = System.Text.Json.JsonSerializer.Deserialize<ISubmodelElement>(child.GetRawText(), BasyxOptions);
                if (sme != null)
                {
                    collection.Add(sme);
                    continue;
                }

                var fallback = CreateFallbackElement(child);
                if (fallback != null)
                {
                    collection.Add(fallback);
                }
            }
        }

        return collection;
    }

    private static ISubmodelElement? CreateFallbackElement(JsonElement element)
    {
        if (!element.TryGetProperty("idShort", out var idShortNode))
        {
            return null;
        }

        var idShort = idShortNode.GetString() ?? string.Empty;
        var modelType = element.TryGetProperty("modelType", out var mtNode) ? mtNode.GetString() : null;

        if (string.Equals(modelType, "SubmodelElementCollection", StringComparison.OrdinalIgnoreCase))
        {
            return BuildNestedCollection(element, idShort);
        }

        if (string.Equals(modelType, "Property", StringComparison.OrdinalIgnoreCase))
        {
            var value = element.TryGetProperty("value", out var valueNode) ? valueNode.GetString() ?? string.Empty : string.Empty;
            return new Property<string>(idShort, value);
        }

        return null;
    }

    private static SubmodelElementCollection BuildNestedCollection(JsonElement element, string idShort)
    {
        var collection = new SubmodelElementCollection(idShort);
        if (element.TryGetProperty("value", out var valNode) && valNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in valNode.EnumerateArray())
            {
                var sme = System.Text.Json.JsonSerializer.Deserialize<ISubmodelElement>(child.GetRawText(), BasyxOptions) ?? CreateFallbackElement(child);
                if (sme != null)
                {
                    collection.Add(sme);
                }
            }
        }

        return collection;
    }

    private static IEnumerable<ISubmodelElement> Elements(SubmodelElementCollection? coll)
    {
        if (coll is null)
        {
            return Array.Empty<ISubmodelElement>();
        }

        if (coll.Value is IEnumerable<ISubmodelElement> seq)
        {
            return seq;
        }

        if (coll is IEnumerable<ISubmodelElement> enumerable)
        {
            return enumerable;
        }

        return Array.Empty<ISubmodelElement>();
    }

    private static SubmodelElementCollection? GetCollection(SubmodelElementCollection coll, string idShort)
    {
        return Elements(coll)
            .OfType<SubmodelElementCollection>()
            .FirstOrDefault(e => string.Equals(e.IdShort, idShort, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetStringProperty(SubmodelElementCollection coll, string idShort, string fallback)
    {
        var property = Elements(coll)
            .FirstOrDefault(e => string.Equals(e.IdShort, idShort, StringComparison.OrdinalIgnoreCase));

        if (property is Property<string> stringProp)
        {
            return stringProp.Value?.Value?.ToString() ?? fallback;
        }

        if (property is IProperty prop && prop.Value?.Value is not null)
        {
            return prop.Value.Value.ToString() ?? fallback;
        }

        return fallback;
    }

    private static Dictionary<string, object> BuildInputParameterDictionary(SubmodelElementCollection? collection)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in Elements(collection).OfType<IProperty>())
        {
            object? raw = element.Value?.Value;
            if (raw is IValue inner)
            {
                raw = inner.Value;
            }

            if (raw != null)
            {
                dict[element.IdShort] = ConvertLooseType(raw);
            }
        }

        return dict;
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

    private static ActionModel CreateActionFromCollection(SubmodelElementCollection coll)
    {
        var title = GetStringProperty(coll, "ActionTitle", "Unknown");
        var statusValue = GetStringProperty(coll, "Status", "planned");
        var machineName = GetStringProperty(coll, "MachineName", string.Empty);

        var status = Enum.TryParse<ActionStatusEnum>(statusValue, true, out var parsedStatus)
            ? parsedStatus
            : ActionStatusEnum.PLANNED;

        var inputParams = InputParameters.FromTypedValues(BuildInputParameterDictionary(GetCollection(coll, "InputParameters")));
        var finalResultData = new FinalResultData(BuildInputParameterDictionary(GetCollection(coll, "FinalResultData")));
        var skillReference = new SkillReference(Array.Empty<(object Key, string Value)>());

        return new ActionModel(
            idShort: coll.IdShort ?? "Action001",
            actionTitle: title,
            status: status,
            inputParameters: inputParams,
            finalResultData: finalResultData,
            skillReference: skillReference,
            machineName: machineName
        );
    }

    private static Dictionary<string, object> ExtractInputParametersDictionary(ActionModel action)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in Elements(action.InputParameters).OfType<IProperty>())
        {
            object? raw = element.Value?.Value;
            if (raw is IValue inner)
            {
                raw = inner.Value;
            }

            if (raw != null)
            {
                dict[element.IdShort] = ConvertLooseType(raw);
            }
        }

        return dict;
    }

    private static object ConvertLooseType(object raw)
    {
        switch (raw)
        {
            case string s:
                if (bool.TryParse(s, out var b)) return b;
                if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)) return i;
                if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return s;
            case JsonElement je:
                return ExtractTypedValueStatic(je, null);
            default:
                return raw;
        }
    }

    private static object ExtractTypedValueStatic(JsonElement element, string? xsdValueType)
    {
        // reuse logic from instance method but static for converter helper
        if (!string.IsNullOrEmpty(xsdValueType))
        {
            var valueStr = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            if (string.IsNullOrEmpty(valueStr)) return string.Empty;

            var normalizedType = xsdValueType.ToLowerInvariant();
            if (normalizedType.StartsWith("xs:")) normalizedType = normalizedType.Substring(3);

            switch (normalizedType)
            {
                case "boolean":
                case "bool":
                    if (bool.TryParse(valueStr, out var boolValue)) return boolValue;
                    if (valueStr == "1") return true;
                    if (valueStr == "0") return false;
                    return false;
                case "integer":
                case "int":
                    if (int.TryParse(valueStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue)) return intValue;
                    return 0;
                case "long":
                    if (long.TryParse(valueStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longValue)) return longValue;
                    return 0L;
                case "double":
                    if (double.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue)) return doubleValue;
                    return 0.0;
                case "float":
                    if (float.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatValue)) return floatValue;
                    return 0.0f;
                case "string":
                default:
                    return valueStr;
            }
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var str = element.GetString() ?? string.Empty;
                if (bool.TryParse(str, out var autoBool)) return autoBool;
                if (int.TryParse(str, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var autoInt)) return autoInt;
                if (double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var autoDouble)) return autoDouble;
                return str;
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var i)) return i;
                if (element.TryGetInt64(out var l)) return l;
                if (element.TryGetDouble(out var d)) return d;
                return element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return string.Empty;
            default:
                return element.GetRawText();
        }
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

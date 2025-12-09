using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Services;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using BaSyx.Models.AdminShell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaSyx.Models.Extensions;
using AasSharpClient.Models;
using UAClient.Client;
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
    private SkillRequestQueue? _skillRequestQueue;
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

        var queue = EnsureQueue();

        if (!_subscribed)
        {
            var topic = $"/Modules/{ModuleId}/SkillRequest/";

            try
            {
                await client.SubscribeAsync(topic);

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

        while (_messageQueue.TryDequeue(out var jsonPayload))
        {
            var envelope = BuildEnvelopeFromJson(jsonPayload);
            if (envelope == null)
            {
                continue;
            }

            if (!queue.TryEnqueue(envelope, out var length))
            {
                Logger.LogWarning(
                    "ReadMqttSkillRequest: Queue full, dropping request for product '{ProductId}' (conversation '{ConversationId}')",
                    envelope.ProductId,
                    envelope.ConversationId);
                continue;
            }

            Logger.LogInformation(
                "ReadMqttSkillRequest: Enqueued Action '{ActionTitle}' for machine '{MachineName}'. Queue length: {Length}",
                envelope.ActionTitle,
                envelope.MachineName,
                length);

            Context.Set("SkillRequestQueueLength", length);
            await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "enqueue", envelope);
        }

        Context.Set("SkillRequestQueueLength", queue.Count);

            if (queue.TryStartNext(out var nextRequest) && nextRequest != null)
            {
                if (!IsModuleLocked(nextRequest.MachineName))
                {
                    Logger.LogInformation(
                        "ReadMqttSkillRequest: Module {MachineName} not locked. Moving conversation '{ConversationId}' to queue end.",
                        nextRequest.MachineName,
                        nextRequest.ConversationId);

                    // Apply a small backoff and move to end so other actions are preferred
                    var startMs = Context.Get<int?>("PreconditionBackoffStartMs") ?? 5000;
                    try
                    {
                        nextRequest.IncrementRetry(TimeSpan.FromMilliseconds(startMs));
                    }
                    catch
                    {
                        // ignore if increment fails for some reason
                    }

                    if (queue.MoveToEndByConversationId(nextRequest.ConversationId, out var moved))
                    {
                        await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "requeue", moved);
                    }

                    Context.Set("SkillRequestQueueLength", queue.Count);
                    return NodeStatus.Running;
                }

            await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "start", nextRequest);
            PopulateContextFromEnvelope(nextRequest);
            Context.Set("SkillRequestQueueLength", queue.Count);

            Logger.LogInformation(
                "ReadMqttSkillRequest: Marked Action '{ActionTitle}' for machine '{MachineName}' (product '{ProductId}') as running.",
                nextRequest.ActionTitle,
                nextRequest.MachineName,
                nextRequest.ProductId);

            return NodeStatus.Success;
        }

        return NodeStatus.Running;
    }
    
    private SkillRequestQueue EnsureQueue()
    {
        if (_skillRequestQueue != null)
        {
            return _skillRequestQueue;
        }

        var queue = Context.Get<SkillRequestQueue>("SkillRequestQueue");
        if (queue == null)
        {
            queue = new SkillRequestQueue();
            Context.Set("SkillRequestQueue", queue);
            Logger.LogWarning("ReadMqttSkillRequest: SkillRequestQueue missing in context. Created new queue instance.");
        }

        _skillRequestQueue = queue;
        return queue;
    }

    private bool IsModuleLocked(string? machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName))
        {
            return false;
        }

        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server != null && server.Modules.TryGetValue(machineName, out var module))
        {
            return module.IsLockedByUs;
        }

        var key = $"State_{machineName}_IsLocked";
        return Context.Get<bool?>(key) ?? false;
    }

    // Removed snapshot publishing helpers (now centralized in ActionQueueBroadcaster).

    private SkillRequestEnvelope? BuildEnvelopeFromJson(string jsonPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("frame", out var frameElement))
            {
                Logger.LogWarning("ReadMqttSkillRequest: No 'frame' found in message");
                return null;
            }

            var conversationId = frameElement.TryGetProperty("conversationId", out var convEl) && convEl.ValueKind == JsonValueKind.String
                ? convEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                Logger.LogWarning("ReadMqttSkillRequest: Missing conversationId in frame (product id not provided)");
            }

            var senderId = ExtractParticipantId(frameElement, "sender");
            var receiverId = ExtractParticipantId(frameElement, "receiver");

            if (!root.TryGetProperty("interactionElements", out var interactionElements))
            {
                Logger.LogWarning("ReadMqttSkillRequest: No 'interactionElements' found in message");
                return null;
            }

            SubmodelElementCollection? actionCollection = null;
            JsonElement? rawActionElement = null;

            foreach (var element in interactionElements.EnumerateArray())
            {
                var raw = element.GetRawText();
                //Logger.LogInformation("ReadMqttSkillRequest: Attempting BaSyx parse of interaction element JSON: {Json}", raw);
                try
                {
                    var collection = BuildCollectionFromElement(element);
                    if (!string.IsNullOrEmpty(collection.IdShort) && collection.IdShort.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                    {
                        actionCollection = collection;
                        rawActionElement = element;
                        break;
                    }

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
                    Logger.LogInformation(
                        "ReadMqttSkillRequest: BaSyx deserialization failed for interaction element; raw JSON: {Json}. Exception: {Ex}",
                        raw,
                        ex.Message);
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
                return null;
            }

            Dictionary<string, object> inputParams;
            ActionModel actionModel;
            string actionTitle;
            string status;
            string machineName;
            string actionIdShort;

            if (actionCollection != null)
            {
                actionModel = CreateActionFromCollection(actionCollection);
                actionTitle = actionModel.ActionTitle.Value.Value?.ToString() ?? string.Empty;
                status = actionModel.State.ToString();
                machineName = actionModel.MachineName.Value.Value?.ToString() ?? string.Empty;
                actionIdShort = actionCollection.IdShort ?? "Action001";
                inputParams = ExtractInputParametersDictionary(actionModel);
            }
            else
            {
                var actionValue = rawActionElement!.Value.GetProperty("value");
                actionTitle = ExtractPropertyValue(actionValue, "ActionTitle");
                status = ExtractPropertyValue(actionValue, "Status");
                machineName = ExtractPropertyValue(actionValue, "MachineName");
                actionIdShort = rawActionElement.Value.TryGetProperty("idShort", out var idShortProp)
                    ? idShortProp.GetString() ?? "Action001"
                    : "Action001";
                inputParams = ExtractInputParametersFromRaw(actionValue);
                actionModel = BuildAasAction(actionIdShort, actionTitle, status, machineName, inputParams);
            }

            PopulatePreconditions(actionModel, actionCollection, rawActionElement);

            return new SkillRequestEnvelope(
                jsonPayload,
                conversationId,
                senderId,
                receiverId,
                actionIdShort,
                actionTitle,
                machineName,
                status,
                inputParams,
                actionModel);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ReadMqttSkillRequest: Failed to parse SkillRequest payload");
            return null;
        }
    }

    private static string ExtractParticipantId(JsonElement frameElement, string propertyName)
    {
        if (!frameElement.TryGetProperty(propertyName, out var participant) || participant.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (participant.TryGetProperty("identification", out var identification) && identification.ValueKind == JsonValueKind.Object)
        {
            if (identification.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String)
            {
                return idValue.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private Dictionary<string, object> ExtractInputParametersFromRaw(JsonElement actionValue)
    {
        var inputParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in actionValue.EnumerateArray())
        {
            if (!prop.TryGetProperty("idShort", out var propIdShort))
            {
                continue;
            }

            if (!string.Equals(propIdShort.GetString(), "InputParameters", StringComparison.Ordinal))
            {
                continue;
            }

            if (!prop.TryGetProperty("value", out var paramsArray) || paramsArray.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var param in paramsArray.EnumerateArray())
            {
                if (!param.TryGetProperty("idShort", out var paramNameElement))
                {
                    continue;
                }

                var paramName = paramNameElement.GetString();
                if (string.IsNullOrEmpty(paramName))
                {
                    continue;
                }

                string? valueType = null;
                if (param.TryGetProperty("valueType", out var valueTypeElement))
                {
                    valueType = valueTypeElement.GetString();
                }

                if (param.TryGetProperty("value", out var paramValue))
                {
                    var typedValue = ExtractTypedValue(paramValue, valueType);
                    inputParams[paramName] = typedValue;
                }
            }

            break;
        }

        return inputParams;
    }

    private void PopulateContextFromEnvelope(SkillRequestEnvelope envelope)
    {
        var parameterCopy = new Dictionary<string, object>(envelope.InputParameters, StringComparer.OrdinalIgnoreCase);

        Context.Set("CurrentSkillRequest", envelope);
        Context.Set("CurrentSkillRequestRawMessage", envelope.RawMessage);
        Context.Set("ConversationId", envelope.ConversationId);
        Context.Set("ProductId", envelope.ProductId);
        Context.Set("RequestSender", envelope.SenderId);
        Context.Set("RequestReceiver", envelope.ReceiverId);
        Context.Set("ActionTitle", envelope.ActionTitle);
        Context.Set("ActionStatus", envelope.ActionStatus);
        Context.Set("MachineName", envelope.MachineName);
        Context.Set("InputParameters", parameterCopy);
        Context.Set("CurrentAction", envelope.ActionModel);
        Context.Set("ActionId", envelope.ActionId);
        if (!string.IsNullOrWhiteSpace(envelope.ConversationId))
        {
            const string ErrorMapKey = "ActionErrorSentMap";
            var errorMap = Context.Get<Dictionary<string, bool>>(ErrorMapKey);
            if (errorMap == null)
            {
                errorMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                Context.Set(ErrorMapKey, errorMap);
            }
            errorMap[envelope.ConversationId] = false;
        }
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
                try
                {
                    var sme = System.Text.Json.JsonSerializer.Deserialize<ISubmodelElement>(child.GetRawText(), BasyxOptions);
                    if (sme != null)
                    {
                        collection.Add(sme);
                        continue;
                    }
                }
                catch (Exception)
                {
                    // deserialization may fail for abstract/interface types (IReference etc.) - fall back to manual parsing
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

        if (string.Equals(modelType, "ReferenceElement", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (element.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Object)
                {
                    var refType = valueEl.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
                    var keyTuples = new List<(KeyType, string)>();
                    if (valueEl.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in keysEl.EnumerateArray())
                        {
                            var kt = k.TryGetProperty("type", out var ktEl) ? ktEl.GetString() ?? string.Empty : string.Empty;
                            var kv = k.TryGetProperty("value", out var kvEl) ? kvEl.GetString() ?? string.Empty : string.Empty;
                            var mapped = MapKeyType(kt);
                            keyTuples.Add((mapped, kv));
                        }
                    }

                    Reference reference = string.Equals(refType, "ModelReference", StringComparison.OrdinalIgnoreCase)
                        ? ReferenceFactory.Model(keyTuples.Select(t => (t.Item1, t.Item2)).ToArray())
                        : ReferenceFactory.External(keyTuples.Select(t => (t.Item1, t.Item2)).ToArray());

                    var refElem = new ReferenceElement(idShort)
                    {
                        Value = new ReferenceElementValue(reference)
                    };

                    return refElem;
                }
            }
            catch
            {
                // ignore and fall through to null
            }
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

    private static void PopulatePreconditions(
        ActionModel actionModel,
        SubmodelElementCollection? actionCollection,
        JsonElement? rawActionElement)
    {
        if (actionModel == null)
        {
            return;
        }

        if (actionCollection != null)
        {
            var preconditions = GetCollection(actionCollection, "Preconditions");
            if (preconditions != null)
            {
                CopyPreconditions(preconditions, actionModel.Preconditions);
                return;
            }
        }

        if (rawActionElement.HasValue)
        {
            var rawPreconditions = ExtractPreconditionsFromRaw(rawActionElement.Value);
            if (rawPreconditions != null)
            {
                CopyPreconditions(rawPreconditions, actionModel.Preconditions);
            }
        }
    }

    private static void CopyPreconditions(SubmodelElementCollection source, SubmodelElementCollection target)
    {
        if (source == null || target == null)
        {
            return;
        }

        foreach (var element in Elements(source))
        {
            target.Add(element);
        }
    }

    private static SubmodelElementCollection? ExtractPreconditionsFromRaw(JsonElement actionElement)
    {
        if (!actionElement.TryGetProperty("value", out var valueArray) || valueArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var element in valueArray.EnumerateArray())
        {
            if (!element.TryGetProperty("idShort", out var idShortNode))
            {
                continue;
            }

            var idShort = idShortNode.GetString();
            if (!string.Equals(idShort, "Preconditions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!element.TryGetProperty("modelType", out var modelTypeNode))
            {
                continue;
            }

            var modelType = modelTypeNode.GetString();
            if (!string.Equals(modelType, "SubmodelElementCollection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return BuildNestedCollection(element, idShort ?? "Preconditions");
        }

        return null;
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
        var preconditions = new AasSharpClient.Models.Preconditions();
        var skillReference = new SkillReference(Array.Empty<(object Key, string Value)>());
        return new AasSharpClient.Models.Action(
            actionId,
            actionTitle,
            mappedStatus,
            inputModel,
            finalResult,
            preconditions,
            skillReference,
            machineName);
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
        var preconditions = new Preconditions();
        var preconditionsSource = GetCollection(coll, "Preconditions");
        if (preconditionsSource != null)
        {
            foreach (var element in Elements(preconditionsSource))
            {
                preconditions.Add(element);
            }
        }

        return new ActionModel(
            idShort: coll.IdShort ?? "Action001",
            actionTitle: title,
            status: status,
            inputParameters: inputParams,
            finalResultData: finalResultData,
            preconditions: preconditions,
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

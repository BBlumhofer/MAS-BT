using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Services;
using MAS_BT.Nodes.Common;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using BaSyx.Models.AdminShell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BaSyx.Models.Extensions;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Tools;
using MAS_BT.Tools;
using UAClient.Client;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// ReadMqttSkillRequest - Liest Action von Planning Agent via MQTT
/// Topic: /{namespace}/{parent_agent}/{subagent}/SkillRequest
/// </summary>
public class ReadMqttSkillRequestNode : BTNode
{
    public string ModuleId { get; set; } = "";
    public int TimeoutMs { get; set; } = 100; // Non-blocking polling
    
    private readonly ConcurrentQueue<string> _messageQueue = new(); // Store raw JSON
    private SkillRequestQueue? _skillRequestQueue;
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

        var queue = EnsureQueue();

        if (!_subscribed)
        {
            var topic = TopicHelper.BuildTopic(Context, "SkillRequest");

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
            // Prefer the I40Sharp MessageSerializer to obtain a typed I40Message with AAS elements
            var serializer = new MessageSerializer();
            I40Message? msg = null;
            try
            {
                msg = serializer.Deserialize(jsonPayload);
            }
            catch (Exception ex)
            {
                Logger.LogInformation("ReadMqttSkillRequest: MessageSerializer failed to deserialize payload: {Msg}", ex.Message);
            }

            if (msg == null)
            {
                Logger.LogWarning("ReadMqttSkillRequest: Failed to parse message as I40Message");
                return null;
            }

            var conversationId = msg.Frame?.ConversationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                Logger.LogWarning("ReadMqttSkillRequest: Missing conversationId in frame (product id not provided)");
            }

            var senderId = msg.Frame?.Sender?.Identification?.Id ?? string.Empty;
            var receiverId = msg.Frame?.Receiver?.Identification?.Id ?? string.Empty;

            var interactionElements = msg.InteractionElements ?? new List<ISubmodelElement>();

            SubmodelElementCollection? actionCollection = null;
            IDictionary<string, object?>? rawActionElement = null;

            foreach (var element in interactionElements)
            {
                if (element is SubmodelElementCollection col && !string.IsNullOrEmpty(col.IdShort) && col.IdShort.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                {
                    actionCollection = col;
                    break;
                }

                // fallback: try to deserialize individual element JSON via JsonLoader
                try
                {
                    var raw = JsonFacade.Serialize(element);
                    var sme = JsonLoader.DeserializeElement(raw);
                    if (sme is SubmodelElementCollection col2 && !string.IsNullOrEmpty(col2.IdShort) && col2.IdShort.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                    {
                        actionCollection = col2;
                        // keep a raw representation for legacy path
                        rawActionElement = new Dictionary<string, object?> { { "idShort", col2.IdShort }, { "value", element } };
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogInformation(
                        "ReadMqttSkillRequest: BaSyx deserialization failed for interaction element; Exception: {Ex}",
                        ex.Message);
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
                actionTitle = actionModel.ActionTitle.GetText() ?? string.Empty;
                status = actionModel.State.ToString();
                machineName = actionModel.MachineName.GetText() ?? string.Empty;
                actionIdShort = actionCollection.IdShort ?? "Action001";
                inputParams = ExtractInputParametersDictionary(actionModel);
            }
            else
            {
                if (rawActionElement is null)
                {
                    return null;
                }

                rawActionElement.TryGetValue("value", out var actionValue);
                actionTitle = ExtractPropertyValue(actionValue, "ActionTitle");
                status = ExtractPropertyValue(actionValue, "Status");
                machineName = ExtractPropertyValue(actionValue, "MachineName");
                actionIdShort = JsonFacade.GetPathAsString(rawActionElement, new[] { "idShort" }) ?? "Action001";
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

    private static string ExtractParticipantId(IDictionary<string, object?> frameElement, string propertyName)
    {
        if (!frameElement.TryGetValue(propertyName, out var participantObj) || participantObj is not IDictionary<string, object?> participant)
        {
            return string.Empty;
        }

        if (participant.TryGetValue("identification", out var identificationObj) && identificationObj is IDictionary<string, object?> identification)
        {
            if (identification.TryGetValue("id", out var idValue))
            {
                return JsonFacade.ToStringValue(idValue) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private Dictionary<string, object> ExtractInputParametersFromRaw(object? actionValue)
    {
        var inputParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (actionValue is not IList<object?> actionElements)
        {
            return inputParams;
        }

        foreach (var propObj in actionElements)
        {
            if (propObj is not IDictionary<string, object?> prop)
            {
                continue;
            }

            var propIdShort = JsonFacade.GetPathAsString(prop, new[] { "idShort" });

            if (!string.Equals(propIdShort, "InputParameters", StringComparison.Ordinal))
            {
                continue;
            }

            if (!prop.TryGetValue("value", out var paramsArrayObj) || paramsArrayObj is not IList<object?> paramsArray)
            {
                break;
            }

            foreach (var paramObj in paramsArray)
            {
                if (paramObj is not IDictionary<string, object?> param)
                {
                    continue;
                }

                var paramName = JsonFacade.GetPathAsString(param, new[] { "idShort" });
                if (string.IsNullOrEmpty(paramName))
                {
                    continue;
                }

                string? valueType = null;
                if (param.TryGetValue("valueType", out var valueTypeObj))
                {
                    valueType = JsonFacade.ToStringValue(valueTypeObj);
                }

                if (param.TryGetValue("value", out var paramValue))
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

    private string ExtractPropertyValue(object? valueArray, string propertyName)
    {
        if (valueArray is not IList<object?> elements)
        {
            return string.Empty;
        }

        foreach (var elementObj in elements)
        {
            if (elementObj is not IDictionary<string, object?> element)
            {
                continue;
            }

            var idShort = JsonFacade.GetPathAsString(element, new[] { "idShort" });
            if (string.Equals(idShort, propertyName, StringComparison.Ordinal))
            {
                if (element.TryGetValue("value", out var value))
                {
                    return JsonFacade.ToStringValue(value) ?? string.Empty;
                }
                return string.Empty;
            }
        }

        return string.Empty;
    }
    
    /// <summary>
    /// Extrahiert einen typisierten Wert aus einem losen Objektwert (primitive/Dictionary/List) und optionalem XSD ValueType.
    /// </summary>
    private object ExtractTypedValue(object? value, string? xsdValueType = null)
    {
        // Wenn ein XSD ValueType angegeben ist, nutze diesen für die Konvertierung
        if (!string.IsNullOrEmpty(xsdValueType))
        {
            if (value is bool or int or long or double or float)
            {
                return value;
            }

            var valueStr = JsonFacade.ToStringValue(value) ?? string.Empty;
            if (string.IsNullOrEmpty(valueStr)) return string.Empty;
            
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
        
        // Fallback: nutze runtime-Typen / heuristische Konvertierung
        switch (value)
        {
            case null:
                return string.Empty;
            case bool:
            case int:
            case long:
            case double:
            case float:
                return value;
            case string s:
                if (bool.TryParse(s, out var autoBool)) return autoBool;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var autoInt)) return autoInt;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var autoLong)) return autoLong;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var autoDouble)) return autoDouble;
                return s;
            case IDictionary<string, object?>:
            case IList<object?>:
                return JsonFacade.Serialize(value);
            default:
                return value.ToString() ?? string.Empty;
        }
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
        IDictionary<string, object?>? rawActionElement)
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

        if (rawActionElement != null)
        {
            var rawPreconditions = ExtractPreconditionsFromRaw(rawActionElement);
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

    private static SubmodelElementCollection? ExtractPreconditionsFromRaw(IDictionary<string, object?> actionElement)
    {
        if (!actionElement.TryGetValue("value", out var valueArrayObj) || valueArrayObj is not IList<object?> valueArray)
        {
            return null;
        }

        foreach (var elementObj in valueArray)
        {
            if (elementObj is not IDictionary<string, object?> element)
            {
                continue;
            }

            var idShort = JsonFacade.GetPathAsString(element, new[] { "idShort" });
            if (!string.Equals(idShort, "Preconditions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = JsonFacade.Serialize(element);
            try
            {
                var sme = JsonLoader.DeserializeElement(raw);
                return sme as SubmodelElementCollection;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string GetStringProperty(SubmodelElementCollection coll, string idShort, string fallback)
    {
        var property = Elements(coll)
            .FirstOrDefault(e => string.Equals(e.IdShort, idShort, StringComparison.OrdinalIgnoreCase));

        if (property is IProperty prop)
        {
            return prop.GetText() ?? fallback;
        }

        return fallback;
    }

    private static Dictionary<string, object> BuildInputParameterDictionary(SubmodelElementCollection? collection)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in Elements(collection).OfType<IProperty>())
        {
            object? raw = AasValueUnwrap.Unwrap(element.Value);

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
            object? raw = AasValueUnwrap.Unwrap(element.Value);

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
            case IDictionary<string, object?>:
            case IList<object?>:
                return JsonFacade.Serialize(raw);
            default:
                return raw;
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
                    var topic = TopicHelper.BuildTopic(Context, "SkillRequest");
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

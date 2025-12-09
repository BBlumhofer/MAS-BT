using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using MAS_BT.Services;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models;
using AasSharpClient.Models.Messages;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// SendSkillResponse - Sendet ActionState Update an Planning Agent
/// Topic: /Modules/{ModuleID}/SkillResponse/
/// Uses I40MessageTypes: consent, refusal (before start), inform (updates), failure (errors during execution)
/// </summary>
public class SendSkillResponseNode : BTNode
{
    public string ModuleId { get; set; } = "";
    public string ActionState { get; set; } = ""; // EXECUTING, DONE, ERROR, ABORTED
    public string FrameType { get; set; } = I40MessageTypes.INFORM; // consent, refusal, inform, failure
    
    public SendSkillResponseNode() : base("SendSkillResponse")
    {
    }
    
    public SendSkillResponseNode(string name) : base(name)
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        // Check if the action was requeued due to preconditions; if so, skip sending ERROR
        var wasRequeued = Context.Get<bool?>("ActionRequeuedDueToPrecondition") ?? false;
        if (wasRequeued && (ActionState.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || FrameType == I40MessageTypes.FAILURE))
        {
            Logger.LogInformation("SendSkillResponse: Action was requeued due to preconditions, skipping ERROR response");
            Context.Set("ActionRequeuedDueToPrecondition", false);
            return NodeStatus.Success;
        }
        Context.Set("ActionRequeuedDueToPrecondition", false);

        Logger.LogDebug("SendSkillResponse: Sending ActionState '{ActionState}' (FrameType: {FrameType}) for module '{ModuleId}'", 
            ActionState, FrameType, ModuleId);
        
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("SendSkillResponse: MessagingClient not found in context");
            return NodeStatus.Failure;
        }
        
        var conversationId = Context.Get<string>("ConversationId");
        var requestSender = Context.Get<string>("RequestSender");
        var actionTitle = Context.Get<string>("ActionTitle");
        var currentRequest = Context.Get<SkillRequestEnvelope>("CurrentSkillRequest");
        const string ErrorMapKey = "ActionErrorSentMap";
        var errorMap = Context.Get<Dictionary<string, bool>>(ErrorMapKey);
        if (errorMap == null)
        {
            errorMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Context.Set(ErrorMapKey, errorMap);
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            conversationId = currentRequest?.ConversationId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(requestSender))
        {
            requestSender = currentRequest?.SenderId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(actionTitle) && currentRequest != null)
        {
            actionTitle = currentRequest.ActionTitle;
        }

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            Context.Set("ConversationId", conversationId);
        }
        if (!string.IsNullOrWhiteSpace(requestSender))
        {
            Context.Set("RequestSender", requestSender);
        }
        
        if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(requestSender))
        {
            Logger.LogError("SendSkillResponse: Missing ConversationId or RequestSender in context");
            return NodeStatus.Failure;
        }
        
        try
        {
            // Hole Original Action aus Context
            var machineName = Context.Get<string>("MachineName");
            if (string.IsNullOrWhiteSpace(machineName) && currentRequest != null)
            {
                machineName = currentRequest.MachineName;
            }

            var rawInputParameters = Context.Get<Dictionary<string, object>>("InputParameters");
            if ((rawInputParameters == null || rawInputParameters.Count == 0) && currentRequest?.InputParameters is { Count: > 0 })
            {
                rawInputParameters = new Dictionary<string, object>(currentRequest.InputParameters, StringComparer.OrdinalIgnoreCase);
            }
            Dictionary<string, string>? inputParameters = null;
            if (rawInputParameters != null && rawInputParameters.Any())
            {
                inputParameters = rawInputParameters.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty);
            }
            var step = Context.Get<string>("Step");
            
            // Mappe ActionState zu gültigen ActionStatusEnum Werten
            var mappedActionState = MapToActionState(ActionState);

            if (ShouldSuppressError(conversationId, mappedActionState, errorMap))
            {
                Logger.LogInformation(
                    "SendSkillResponse: Error already sent for conversation '{ConversationId}', skipping duplicate",
                    conversationId);
                return NodeStatus.Success;
            }
            
            // Erstelle I4.0 Message
            var messageBuilder = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To(requestSender, "PlanningAgent")
                .WithType(FrameType)
                .WithConversationId(conversationId);
            
            // Note: messageId / replyTo are considered obsolete in our protocol.
            // We rely solely on ConversationId to correlate requests and responses.
            
            var statusValue = mappedActionState.ToLowerInvariant();

            IDictionary<string, string>? inputParametersDict = null;
            if (inputParameters != null && inputParameters.Any())
            {
                inputParametersDict = inputParameters;
            }

            var includeStep = FrameType == I40MessageTypes.REFUSAL ? step : null;
            string? logMessage = null;
            if (FrameType == I40MessageTypes.REFUSAL || FrameType == I40MessageTypes.FAILURE)
            {
                logMessage = Context.Get<string>("LogMessage") ?? Context.Get<string>("ErrorMessage");
                if (!string.IsNullOrWhiteSpace(logMessage))
                {
                    Logger.LogInformation("SendSkillResponse: Including LogMessage: {LogMessage}", logMessage);
                }
            }

            IDictionary<string, object?>? finalResultData = null;
            long? successfulExecutions = null;
            if (mappedActionState == ActionStatusEnum.DONE.ToString() && !string.IsNullOrEmpty(actionTitle))
            {
                finalResultData = Context.Get<IDictionary<string, object?>>($"Skill_{actionTitle}_FinalResultData");
                if (finalResultData is { Count: > 0 })
                {
                    Logger.LogInformation("SendSkillResponse: Including {Count} FinalResultData entries", finalResultData.Count);
                }
                else
                {
                    finalResultData = null;
                }

                successfulExecutions = Context.Get<long?>($"Skill_{actionTitle}_SuccessfulExecutionsCount");
            }

            var skillResponse = new SkillResponseMessage(
                mappedActionState,
                statusValue,
                actionTitle,
                machineName,
                includeStep,
                inputParametersDict,
                finalResultData,
                logMessage,
                successfulExecutions);

            messageBuilder.AddElement(skillResponse);
            
            var message = messageBuilder.Build();
            
            var topic = $"/Modules/{ModuleId}/SkillResponse/";
            await client.PublishAsync(message, topic);
            
            Logger.LogInformation("SendSkillResponse: Sent ActionState '{ActionState}' (FrameType: {FrameType}) to '{Receiver}' on topic '{Topic}'", 
                mappedActionState, FrameType, requestSender, topic);

            TrackErrorState(conversationId, mappedActionState, errorMap);

            await RemoveQueueEntryIfTerminalAsync(mappedActionState, conversationId, client);
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendSkillResponse: Failed to send response");
            return NodeStatus.Failure;
        }
    }
    
    /// <summary>
    /// Mappt Skill States oder andere Werte zu gültigen ActionStatusEnum Werten
    /// </summary>
    private string MapToActionState(string state)
    {
        if (string.IsNullOrEmpty(state))
            return ActionStatusEnum.OPEN.ToString();
            
        // Direkt gültige ActionStates
        if (Enum.TryParse<ActionStatusEnum>(state.ToUpperInvariant(), out var enumValue))
            return enumValue.ToString();
            
        // Mapping von Skill States zu Action States
        return state.ToUpperInvariant() switch
        {
            "COMPLETED" => ActionStatusEnum.DONE.ToString(),
            "RUNNING" => ActionStatusEnum.EXECUTING.ToString(),
            "STARTING" => ActionStatusEnum.EXECUTING.ToString(),
            "HALTED" => ActionStatusEnum.ABORTED.ToString(),
            "READY" => ActionStatusEnum.PLANNED.ToString(),
            "SUSPENDED" => ActionStatusEnum.SUSPENDED.ToString(),
            _ => state.ToUpperInvariant()
        };
    }
    
    public override Task OnAbort()
    {
        return Task.CompletedTask;
    }
    
    public override Task OnReset()
    {
        return Task.CompletedTask;
    }

    private async Task RemoveQueueEntryIfTerminalAsync(string mappedActionState, string conversationId, MessagingClient client)
    {
        if (!IsTerminalActionState(mappedActionState))
        {
            return;
        }

        var queue = Context.Get<SkillRequestQueue>("SkillRequestQueue");
        if (queue == null)
        {
            return;
        }

        if (queue.TryRemoveByConversationId(conversationId, out var removed))
        {
            await ActionQueueBroadcaster.PublishSnapshotAsync(Context, client, queue, "remove", removed);
            Logger.LogInformation("SendSkillResponse: Removed conversation '{ConversationId}' from SkillRequestQueue after state {State}", conversationId, mappedActionState);
        }

        const string ErrorMapKey = "ActionErrorSentMap";
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            var errorMap = Context.Get<Dictionary<string, bool>>(ErrorMapKey);
            errorMap?.Remove(conversationId);
        }

    }

    private static bool IsTerminalActionState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        return state.Equals(ActionStatusEnum.DONE.ToString(), StringComparison.OrdinalIgnoreCase)
               || state.Equals(ActionStatusEnum.ABORTED.ToString(), StringComparison.OrdinalIgnoreCase)
               || state.Equals(ActionStatusEnum.ERROR.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSuppressError(
        string conversationId,
        string mappedActionState,
        IDictionary<string, bool> errorMap)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        if (!IsErrorActionState(mappedActionState))
        {
            return false;
        }

        return errorMap.TryGetValue(conversationId, out var sent) && sent;
    }

    private static void TrackErrorState(
        string conversationId,
        string mappedActionState,
        IDictionary<string, bool> errorMap)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        if (!IsErrorActionState(mappedActionState))
        {
            return;
        }

        errorMap[conversationId] = true;
    }

    private static bool IsErrorActionState(string mappedActionState)
    {
        return mappedActionState.Equals(ActionStatusEnum.ERROR.ToString(), StringComparison.OrdinalIgnoreCase);
    }

}
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
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
        Logger.LogDebug("SendSkillResponse: Sending ActionState '{ActionState}' (FrameType: {FrameType}) for module '{ModuleId}'", 
            ActionState, FrameType, ModuleId);
        
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null)
        {
            Logger.LogError("SendSkillResponse: MessagingClient not found in context");
            return NodeStatus.Failure;
        }
        
        var conversationId = Context.Get<string>("ConversationId");
        var originalMessageId = Context.Get<string>("OriginalMessageId");
        var requestSender = Context.Get<string>("RequestSender");
        var actionTitle = Context.Get<string>("ActionTitle");
        
        if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(requestSender))
        {
            Logger.LogError("SendSkillResponse: Missing ConversationId or RequestSender in context");
            return NodeStatus.Failure;
        }
        
        try
        {
            // Hole Original Action aus Context
            var machineName = Context.Get<string>("MachineName");
            var rawInputParameters = Context.Get<Dictionary<string, object>>("InputParameters");
            Dictionary<string, string>? inputParameters = null;
            if (rawInputParameters != null && rawInputParameters.Any())
            {
                inputParameters = rawInputParameters.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty);
            }
            var step = Context.Get<string>("Step");
            
            // Mappe ActionState zu gültigen ActionStatusEnum Werten
            var mappedActionState = MapToActionState(ActionState);
            
            // Erstelle I4.0 Message
            var messageBuilder = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To(requestSender, "PlanningAgent")
                .WithType(FrameType)
                .WithConversationId(conversationId);
            
            if (!string.IsNullOrEmpty(originalMessageId))
            {
                messageBuilder.ReplyingTo(originalMessageId);
            }
            
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
}
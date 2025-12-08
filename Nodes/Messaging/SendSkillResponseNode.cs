using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using AasSharpClient.Models;

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
            var inputParameters = Context.Get<Dictionary<string, string>>("InputParameters");
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
            
            // Füge Properties mit VALUES hinzu (korrekte BaSyx Syntax)
            
            // ActionState
            var actionStateProp = new Property<string>("ActionState");
            actionStateProp.Value = new PropertyValue<string>(mappedActionState);
            messageBuilder.AddElement(actionStateProp);
            
            // ActionTitle
            if (!string.IsNullOrEmpty(actionTitle))
            {
                var titleProp = new Property<string>("ActionTitle");
                titleProp.Value = new PropertyValue<string>(actionTitle);
                messageBuilder.AddElement(titleProp);
            }
            
            // Status (für Legacy-Kompatibilität)
            var statusValue = mappedActionState.ToLowerInvariant();
            var statusProp = new Property<string>("Status");
            statusProp.Value = new PropertyValue<string>(statusValue);
            messageBuilder.AddElement(statusProp);
            
            // MachineName
            if (!string.IsNullOrEmpty(machineName))
            {
                var machineNameProp = new Property<string>("MachineName");
                machineNameProp.Value = new PropertyValue<string>(machineName);
                messageBuilder.AddElement(machineNameProp);
            }
            
            // Step (bei refusal mitschicken)
            if (!string.IsNullOrEmpty(step) && FrameType == I40MessageTypes.REFUSAL)
            {
                var stepProp = new Property<string>("Step");
                stepProp.Value = new PropertyValue<string>(step);
                messageBuilder.AddElement(stepProp);
            }
            
            // InputParameters als SubmodelElementCollection
            if (inputParameters != null && inputParameters.Any())
            {
                var inputParamsCollection = new SubmodelElementCollection("InputParameters");
                foreach (var kvp in inputParameters)
                {
                    var prop = new Property<string>(kvp.Key);
                    prop.Value = new PropertyValue<string>(kvp.Value);
                    inputParamsCollection.Add(prop);
                }
                messageBuilder.AddElement(inputParamsCollection);
            }
            
            // LogMessage (bei refusal oder failure)
            if (FrameType == I40MessageTypes.REFUSAL || FrameType == I40MessageTypes.FAILURE)
            {
                var logMessage = Context.Get<string>("LogMessage") ?? Context.Get<string>("ErrorMessage");
                if (!string.IsNullOrEmpty(logMessage))
                {
                    var logProp = new Property<string>("LogMessage");
                    logProp.Value = new PropertyValue<string>(logMessage);
                    messageBuilder.AddElement(logProp);
                    Logger.LogInformation("SendSkillResponse: Including LogMessage: {LogMessage}", logMessage);
                }
            }
            
            // FinalResultData (nur bei DONE State)
            if (mappedActionState == "DONE")
            {
                var skillName = actionTitle; // ActionTitle = SkillName
                var finalResultData = Context.Get<IDictionary<string, object>>($"Skill_{skillName}_FinalResultData");
                
                if (finalResultData != null && finalResultData.Any())
                {
                    var resultCollection = new SubmodelElementCollection("FinalResultData");
                    foreach (var kvp in finalResultData)
                    {
                        var prop = new Property<string>(kvp.Key);
                        prop.Value = new PropertyValue<string>(kvp.Value?.ToString() ?? "");
                        resultCollection.Add(prop);
                    }
                    messageBuilder.AddElement(resultCollection);
                    
                    Logger.LogInformation("SendSkillResponse: Including {Count} FinalResultData entries", finalResultData.Count);
                }
            }
            
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
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using AasSharpClient.Models;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// SendSkillResponse - Sendet ActionState Update an Planning Agent
/// Topic: /Modules/{ModuleID}/SkillResponse/
/// Verwendet Action aus AAS-Sharp-Client für korrekte Serialisierung
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
        
        if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(requestSender))
        {
            Logger.LogError("SendSkillResponse: Missing ConversationId or RequestSender in context");
            return NodeStatus.Failure;
        }
        
        try
        {
            // Hole Action aus Context (wurde von ParseActionRequest erstellt)
            var action = Context.Get<AasSharpClient.Models.Action>("CurrentAction");
            if (action == null)
            {
                Logger.LogError("SendSkillResponse: No Action found in context");
                return NodeStatus.Failure;
            }
            
            // Mappe ActionState zu gültigen ActionStatusEnum Werten
            var mappedState = MapToActionState(ActionState);
            
            // Setze Action Status (die Action-Klasse übernimmt die Serialisierung!)
            action.SetStatus(mappedState);
            
            // Erstelle I4.0 Message mit Action
            var messageBuilder = new I40MessageBuilder()
                .From($"{ModuleId}_Execution_Agent", "ExecutionAgent")
                .To(requestSender, "PlanningAgent")
                .WithType(FrameType)
                .WithConversationId(conversationId);
            
            if (!string.IsNullOrEmpty(originalMessageId))
            {
                messageBuilder.ReplyingTo(originalMessageId);
            }
            
            // Füge die Action direkt hinzu (enthält alle Properties mit korrekten Values!)
            messageBuilder.AddElement(action);
            
            // LogMessage (Optional bei Refusal/Failure)
            if (FrameType == I40MessageTypes.REFUSAL || FrameType == I40MessageTypes.FAILURE)
            {
                var logMessage = Context.Get<string>("LogMessage") ?? Context.Get<string>("ErrorMessage");
                if (!string.IsNullOrEmpty(logMessage))
                {
                    var logProp = I40MessageBuilder.CreateStringProperty("LogMessage", logMessage);
                    messageBuilder.AddElement(logProp);
                    Logger.LogInformation("SendSkillResponse: Including LogMessage: {LogMessage}", logMessage);
                }
            }
            
            var message = messageBuilder.Build();
            
            var topic = $"/Modules/{ModuleId}/SkillResponse/";
            await client.PublishAsync(message, topic);
            
            Logger.LogInformation("SendSkillResponse: Sent ActionState '{ActionState}' (FrameType: {FrameType}) to '{Receiver}' on topic '{Topic}'", 
                mappedState, FrameType, requestSender, topic);
            
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
    private ActionStatusEnum MapToActionState(string state)
    {
        if (string.IsNullOrEmpty(state))
            return ActionStatusEnum.OPEN;
            
        // Direkt gültige ActionStates
        if (Enum.TryParse<ActionStatusEnum>(state.ToUpperInvariant(), out var enumValue))
            return enumValue;
            
        // Mapping von Skill States zu Action States
        return state.ToUpperInvariant() switch
        {
            "COMPLETED" => ActionStatusEnum.DONE,
            "RUNNING" => ActionStatusEnum.EXECUTING,
            "STARTING" => ActionStatusEnum.EXECUTING,
            "HALTED" => ActionStatusEnum.ABORTED,
            "READY" => ActionStatusEnum.PLANNED,
            "SUSPENDED" => ActionStatusEnum.SUSPENDED,
            _ => ActionStatusEnum.OPEN
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
using System;
using System.Collections.Generic;
using I40Sharp.Messaging.Models;
using System.Linq;
using BaSyx.Models.AdminShell;
using System;

public static class ActionUpdateBuilder
{
    // Builds a simple I4.0-style ActionUpdate message for precondition retries.
    public static I40Message BuildPreconditionUpdate(object envelopeObj, string reason, int attempts, DateTime? nextRetry)
    {
        try
        {
            var conv = (dynamic)envelopeObj;
            var conversationId = conv.ConversationId as string ?? string.Empty;
            var actionTitle = conv.ActionTitle as string ?? string.Empty;
            var senderId = conv.SenderId as string ?? string.Empty;

            var msg = new I40Message();
            msg.Frame = new MessageFrame
            {
                Type = "update",
                ConversationId = conversationId,
                Sender = new Participant { Identification = new Identification { Id = "ExecutionAgent" }, Role = new Role { Name = "ExecutionAgent" } },
                Receiver = new Participant { Identification = new Identification { Id = senderId }, Role = new Role { Name = "PlanningAgent" } }
            };

            var elems = new List<ISubmodelElement>();
            elems.Add(new Property<string>("ActionTitle", actionTitle));
            elems.Add(new Property<string>("ActionState", "PRECONDITION_RETRY"));
            elems.Add(new Property<string>("Reason", reason));
            elems.Add(new Property<int>("Attempts", attempts));
            elems.Add(new Property<string>("NextRetryUtc", nextRetry?.ToString("o") ?? string.Empty));

            msg.InteractionElements = elems.Cast<ISubmodelElement>().ToList();

            return msg;
        }
        catch
        {
            var fallback = new I40Message();
            fallback.Frame = new MessageFrame { Type = "update", ConversationId = string.Empty };
            fallback.InteractionElements = new List<ISubmodelElement>();
            return fallback;
        }
    }
}

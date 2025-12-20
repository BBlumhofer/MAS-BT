using System;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Common
{
    /// <summary>
    /// Generic registration handler.
    /// Consumes Context[LastReceivedMessage] and upserts DispatchingState.
    /// </summary>
    public class HandleRegistrationNode : BTNode
    {
        public HandleRegistrationNode() : base("HandleRegistration") { }

        public override Task<NodeStatus> Execute()
        {
            var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
            Context.Set("DispatchingState", state);

            var message = Context.Get<I40Message>("LastReceivedMessage");
            if (message == null)
            {
                Logger.LogWarning("HandleRegistration: no message in context");
                return Task.FromResult(NodeStatus.Failure);
            }

            var moduleId = message.Frame?.Sender?.Identification?.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(moduleId))
            {
                moduleId = message.Frame?.Receiver?.Identification?.Id ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                Logger.LogWarning("HandleRegistration: unable to extract ModuleId from message");
                return Task.FromResult(NodeStatus.Failure);
            }

            var info = DispatchingModuleInfo.FromMessage(message);
            if (string.IsNullOrWhiteSpace(info.ModuleId))
            {
                info.ModuleId = moduleId;
            }
            info.LastRegistrationUtc = DateTime.UtcNow;
            info.LastSeenUtc = info.LastRegistrationUtc;

            state.Upsert(info);
            Context.Set("LastRegisteredModuleId", moduleId);

            if (info.Capabilities.Count > 0)
            {
                Logger.LogInformation(
                    "HandleRegistration: registered/updated sub-agent '{ModuleId}' with {Count} capabilities: [{Capabilities}]",
                    moduleId,
                    info.Capabilities.Count,
                    string.Join(", ", info.Capabilities));
            }
            else
            {
                Logger.LogWarning("HandleRegistration: registered/updated sub-agent '{ModuleId}' with ZERO capabilities. " +
                               "Sender: {SenderId}, Role: {SenderRole}. " +
                               "This may indicate that the agent has not yet loaded its AAS submodels or capability extraction failed.",
                               moduleId,
                               message.Frame?.Sender?.Identification?.Id ?? "<unknown>",
                               message.Frame?.Sender?.Role?.Name ?? "<unknown>");
            }

            return Task.FromResult(NodeStatus.Success);
        }
    }
}

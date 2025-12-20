using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.Planning.ProcessChain;

public class ParseCapabilityRequestNode : BTNode
{
    public ParseCapabilityRequestNode() : base("ParseCapabilityRequest") { }

    public override Task<NodeStatus> Execute()
    {
        var incoming = Context.Get<I40Message>("LastReceivedMessage");
        if (incoming == null)
        {
            Logger.LogDebug("ParseCapabilityRequest: no incoming message");
            return Task.FromResult(NodeStatus.Failure);
        }

        var request = CapabilityRequestContext.FromMessage(incoming);
        if (string.IsNullOrWhiteSpace(request.Capability))
        {
            Logger.LogDebug("ParseCapabilityRequest: capability missing in CfP message");
            return Task.FromResult(NodeStatus.Failure);
        }

        // Guard: check if this conversation was already processed to prevent reprocessing loops
        var processedKey = $"Planning.Processed:{request.ConversationId}";
        if (Context.Get<bool>(processedKey))
        {
            Logger.LogDebug("ParseCapabilityRequest: conversation {Conv} already processed, skipping", request.ConversationId);
            // Clear message to prevent infinite loop
            Context.Set("LastReceivedMessage", (I40Message?)null);
            Context.Set("CurrentMessage", (I40Message?)null);
            return Task.FromResult(NodeStatus.Failure);
        }

        Context.Set("Planning.CapabilityRequest", request);
        Context.Set("RequiredCapability", request.Capability);
        Context.Set("RequesterId", request.RequesterId);
        Context.Set("RequesterRole", request.RequesterRole);
        Context.Set("ConversationId", request.ConversationId);
        
        // Mark conversation as being processed
        Context.Set(processedKey, true);
        
        // Clear CurrentMessage immediately after parsing to prevent reprocessing in the same loop iteration
        Context.Set("CurrentMessage", (I40Message?)null);
        
        Logger.LogInformation("ParseCapabilityRequest: capability={Capability} requirement={Requirement}", request.Capability, request.RequirementId);
        return Task.FromResult(NodeStatus.Success);
    }
}

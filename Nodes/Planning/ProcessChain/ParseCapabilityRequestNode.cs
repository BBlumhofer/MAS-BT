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
            Logger.LogWarning("ParseCapabilityRequest: no incoming message");
            return Task.FromResult(NodeStatus.Failure);
        }

        var request = CapabilityRequestContext.FromMessage(incoming);
        if (string.IsNullOrWhiteSpace(request.Capability))
        {
            Logger.LogWarning("ParseCapabilityRequest: capability missing in CfP message");
            return Task.FromResult(NodeStatus.Failure);
        }

        Context.Set("Planning.CapabilityRequest", request);
        Context.Set("RequiredCapability", request.Capability);
        Context.Set("RequesterId", request.RequesterId);
        Context.Set("ConversationId", request.ConversationId);

        Logger.LogInformation("ParseCapabilityRequest: capability={Capability} requirement={Requirement}", request.Capability, request.RequirementId);
        return Task.FromResult(NodeStatus.Success);
    }
}

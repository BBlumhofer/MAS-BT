using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// FeedbackCapabilityMatchmaking - stub: assumes matchmaking result already in context, validates it.
/// </summary>
public class FeedbackCapabilityMatchmakingNode : BTNode
{
    public string RefusalReason { get; set; } = "capability_not_found";

    public FeedbackCapabilityMatchmakingNode() : base("FeedbackCapabilityMatchmaking") {}

    public override Task<NodeStatus> Execute()
    {
        var matched = Context.Get<string>("MatchedCapability") ?? Context.Get<string>("LastMatchedCapability") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(matched))
        {
            Logger.LogWarning("FeedbackCapabilityMatchmaking: no matched capability in context");
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }
        Logger.LogInformation("FeedbackCapabilityMatchmaking: matched={Capability}", matched);
        Context.Set("DerivedCapability", matched);
        return Task.FromResult(NodeStatus.Success);
    }
}

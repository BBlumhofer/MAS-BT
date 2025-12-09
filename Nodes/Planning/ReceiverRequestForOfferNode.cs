using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// ReceiverRequestForOffer - stub: initializes planning context for an incoming capability request.
/// Expects RequiredCapability (string) optionally from context; if missing, fails and sets RefusalReason.
/// </summary>
public class ReceiverRequestForOfferNode : BTNode
{
    public string RequiredCapability { get; set; } = string.Empty;
    public string RefusalReason { get; set; } = "missing_required_capability";

    public ReceiverRequestForOfferNode() : base("ReceiverRequestForOffer") {}

    public override Task<NodeStatus> Execute()
    {
        var reqCap = !string.IsNullOrWhiteSpace(RequiredCapability)
            ? ResolvePlaceholders(RequiredCapability)
            : Context.Get<string>("RequiredCapability") ?? Context.Get<string>("ActionTitle") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(reqCap))
        {
            Logger.LogWarning("ReceiverRequestForOffer: no RequiredCapability provided");
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }

        Context.Set("RequiredCapability", reqCap);
        Logger.LogInformation("ReceiverRequestForOffer: capability={Capability}", reqCap);
        return Task.FromResult(NodeStatus.Success);
    }
}

using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// EvaluateRequestTransportResponse - stub: marks transport request as accepted or failed based on context flag.
/// </summary>
public class EvaluateRequestTransportResponseNode : BTNode
{
    public string RefusalReason { get; set; } = "transport_not_available";

    public EvaluateRequestTransportResponseNode() : base("EvaluateRequestTransportResponse") {}

    public override Task<NodeStatus> Execute()
    {
        var transportOk = Context.Get<bool?>("TransportAccepted") ?? true; // default optimistic
        if (!transportOk)
        {
            Logger.LogWarning("EvaluateRequestTransportResponse: transport denied");
            Context.Set("RefusalReason", RefusalReason);
            return Task.FromResult(NodeStatus.Failure);
        }
        Logger.LogInformation("EvaluateRequestTransportResponse: transport accepted");
        Context.Set("TransportAccepted", true);
        return Task.FromResult(NodeStatus.Success);
    }
}

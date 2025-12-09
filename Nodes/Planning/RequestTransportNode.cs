using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// RequestTransport - stub: logs transport request parameters.
/// </summary>
public class RequestTransportNode : BTNode
{
    public string FromStation { get; set; } = string.Empty;
    public string ToStation { get; set; } = string.Empty;

    public RequestTransportNode() : base("RequestTransport") {}

    public override Task<NodeStatus> Execute()
    {
        var requires = Context.Get<bool?>("RequiresTransport") ?? false;
        if (!requires)
        {
            Logger.LogDebug("RequestTransport: no transport required, skipping");
            return Task.FromResult(NodeStatus.Success);
        }
        var from = ResolvePlaceholders(FromStation);
        var to = ResolvePlaceholders(ToStation);
        Logger.LogInformation("RequestTransport: from={From} to={To}", from, to);
        Context.Set("LastTransportRequest", new { From = from, To = to });
        return Task.FromResult(NodeStatus.Success);
    }
}

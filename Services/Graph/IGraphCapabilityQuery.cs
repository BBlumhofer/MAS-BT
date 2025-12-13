using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MAS_BT.Services.Graph;

public interface IGraphCapabilityQuery
{
    Task<bool> AnyRegisteredAgentImplementsAllAsync(
        string @namespace,
        IReadOnlyCollection<string> requiredCapabilities,
        IReadOnlyCollection<string> registeredAgentIds,
        CancellationToken cancellationToken = default);
}

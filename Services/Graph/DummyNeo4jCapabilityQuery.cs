using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MAS_BT.Services.Graph;

/// <summary>
/// Dummy implementation used until the Neo4j graph schema/query is finalized.
/// Always returns true.
/// </summary>
public sealed class DummyNeo4jCapabilityQuery : IGraphCapabilityQuery
{
    public Task<bool> AnyRegisteredAgentImplementsAllAsync(
        string @namespace,
        IReadOnlyCollection<string> requiredCapabilities,
        IReadOnlyCollection<string> registeredAgentIds,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

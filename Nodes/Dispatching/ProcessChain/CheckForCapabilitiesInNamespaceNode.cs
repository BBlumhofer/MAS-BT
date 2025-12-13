using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Services.Graph;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Nodes.Dispatching.ProcessChain;

public class CheckForCapabilitiesInNamespaceNode : BTNode
{
    public CheckForCapabilitiesInNamespaceNode() : base("CheckForCapabilitiesInNamespace") { }

    public override async Task<NodeStatus> Execute()
    {
        var negotiation = Context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
        if (negotiation == null)
        {
            Logger.LogError("CheckForCapabilitiesInNamespace: negotiation context missing");
            Context.Set("ProcessChain.RefusalReason", "Missing process chain negotiation context");
            Context.Set("ProcessChain.Success", false);
            return NodeStatus.Failure;
        }

        var requiredCapabilities = negotiation.Requirements
            .Select(r => r.Capability)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredCapabilities.Count == 0)
        {
            Logger.LogWarning("CheckForCapabilitiesInNamespace: no required capabilities in request");
            Context.Set("ProcessChain.RefusalReason", "No required capabilities in request");
            Context.Set("ProcessChain.Success", false);
            return NodeStatus.Failure;
        }

        var state = Context.Get<DispatchingState>("DispatchingState");
        var registeredAgentIds = state?.Modules
            .Select(m => m.ModuleId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        Context.Set("Dispatching.RegisteredAgents", registeredAgentIds);

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? string.Empty;

        var query = Context.Get<IGraphCapabilityQuery>("GraphCapabilityQuery") ?? new DummyNeo4jCapabilityQuery();

        // Dummy query always returns true; keep cancellation token hook for future real driver.
        var result = await query.AnyRegisteredAgentImplementsAllAsync(
            ns,
            requiredCapabilities,
            registeredAgentIds,
            CancellationToken.None);

        if (result)
        {
            Logger.LogInformation("CheckForCapabilitiesInNamespace: capabilities are available in namespace {Namespace}", ns);
            return NodeStatus.Success;
        }

        Logger.LogWarning("CheckForCapabilitiesInNamespace: missing capability providers in namespace {Namespace}", ns);
        Context.Set("ProcessChain.RefusalReason", "No registered agent implements the required capabilities");
        Context.Set("ProcessChain.Success", false);
        return NodeStatus.Failure;
    }
}

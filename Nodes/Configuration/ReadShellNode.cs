using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaSyx.Clients.AdminShell.Http;
using BaSyx.Models.AdminShell;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// ReadShell - Loads the entire AAS for initialization or re-synchronization
/// </summary>
public class ReadShellNode : BTNode
{
    public string AgentId { get; set; } = string.Empty;
    public string ShellRepositoryEndpoint { get; set; } = string.Empty;
    
    public ReadShellNode() : base("ReadShell")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        var resolvedAgentId = ResolveAgentId();
        if (string.IsNullOrWhiteSpace(resolvedAgentId))
        {
            Logger.LogError("ReadShell: Unable to determine AgentId. Provide AgentId property or config.Agent.AgentId");
            return NodeStatus.Failure;
        }

        var endpoint = ResolveShellEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Logger.LogError("ReadShell: ShellRepositoryEndpoint missing. Set property ShellRepositoryEndpoint or config.AAS.ShellRepositoryEndpoint");
            return NodeStatus.Failure;
        }

        Logger.LogInformation("ReadShell: Reading AAS {AgentId} from {Endpoint}", resolvedAgentId, endpoint);
        
        try
        {
            using var client = new AssetAdministrationShellRepositoryHttpClient(BuildUri(endpoint));
            var result = await client.RetrieveAssetAdministrationShellAsync(new Identifier(resolvedAgentId)).ConfigureAwait(false);
            if (!result.Success || result.Entity == null)
            {
                var message = result.Messages?.ToString() ?? "(no message)";
                Logger.LogError("ReadShell: Failed to retrieve shell {AgentId}: {Message}", resolvedAgentId, message);
                return NodeStatus.Failure;
            }

            var shell = result.Entity;
            // SubmodelReferences may come back as non-generic Reference instances, so keep the concrete type without casting
            var references = shell.SubmodelReferences?.Cast<IReference>().ToList() ?? new List<IReference>();
            var referenceCount = references.Count;

            Context.Set("shell", shell);
            Context.Set("AAS.Shell", shell);
            Context.Set("AAS.Shell.SubmodelReferences", references);
            Context.Set("AAS.ShellRepositoryEndpoint", endpoint);

            // IMPORTANT: Do not clobber the agent identity when we load *another* shell.
            // Example: PlanningHolon loads the ModuleHolon shell (AgentId=ModuleId/ModuleName) for capability data.
            // In that case, overwriting Context.AgentId / "AgentId" would break registrations and messaging.
            var configuredAgentId = Context.Get<string>("config.Agent.AgentId") ?? string.Empty;
            var currentAgentId = Context.AgentId ?? string.Empty;
            var blackboardAgentId = Context.Get<string>("AgentId") ?? string.Empty;

            var looksLikeSelf =
                (!string.IsNullOrWhiteSpace(configuredAgentId) && string.Equals(resolvedAgentId, configuredAgentId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(currentAgentId) && string.Equals(resolvedAgentId, currentAgentId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(blackboardAgentId) && string.Equals(resolvedAgentId, blackboardAgentId, StringComparison.OrdinalIgnoreCase));

            if (looksLikeSelf)
            {
                Context.AgentId = resolvedAgentId;
                Context.Set("AgentId", resolvedAgentId);
            }
            else
            {
                Context.Set("AAS.LoadedShellAgentId", resolvedAgentId);
            }

            Logger.LogInformation("ReadShell: Loaded shell {ShellIdShort} ({ShellId}) with {Submodels} submodel references",
                shell.IdShort,
                shell.Id?.Id ?? resolvedAgentId,
                referenceCount);
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ReadShell: Error reading AAS for {AgentId}", resolvedAgentId);
            return NodeStatus.Failure;
        }
    }

    private string ResolveAgentId()
    {
        if (!string.IsNullOrWhiteSpace(AgentId))
        {
            return ResolvePlaceholders(AgentId);
        }

        var configAgentId = Context.Get<string>("config.Agent.AgentId");
        if (!string.IsNullOrWhiteSpace(configAgentId))
        {
            return configAgentId;
        }

        if (!string.Equals(Context.AgentId, "UnknownAgent", StringComparison.OrdinalIgnoreCase))
        {
            return Context.AgentId;
        }

        return Context.Get<string>("AgentId") ?? string.Empty;
    }

    private string ResolveShellEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(ShellRepositoryEndpoint))
        {
            return ResolvePlaceholders(ShellRepositoryEndpoint);
        }

        return Context.Get<string>("config.AAS.ShellRepositoryEndpoint")
            ?? Context.Get<string>("AAS.ShellRepositoryEndpoint")
            ?? string.Empty;
    }

    private static Uri BuildUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid ShellRepositoryEndpoint URI: {endpoint}");
        }

        return uri;
    }
}

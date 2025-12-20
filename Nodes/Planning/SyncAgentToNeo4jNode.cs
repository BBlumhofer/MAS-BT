using System;
using System.Linq;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// Synchronisiert einen Agenten (aus RegisterMessage) in Neo4j.
/// Erstellt/aktualisiert Agent-Node und hierarchische Relationships (IS_SUBAGENT_OF, MANAGES_ASSET).
/// </summary>
public class SyncAgentToNeo4jNode : BTNode
{
    public SyncAgentToNeo4jNode() : base("SyncAgentToNeo4j") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var driver = Context.Get<IDriver>("Neo4jDriver");
            if (driver == null)
            {
                Logger.LogWarning("SyncAgentToNeo4j: Neo4jDriver not available in context");
                return NodeStatus.Failure;
            }

            var database = Context.Get<string>("config.Neo4j.Database")
                         ?? Environment.GetEnvironmentVariable("NEO4J_DATABASE")
                         ?? "neo4j";

            // Try to get RegisterMessage from LastReceivedMessage, or build from context
            var message = Context.Get<I40Message>("LastReceivedMessage");
            RegisterMessage? registerMsg = null;
            string agentId = string.Empty;
            string role = string.Empty;
            DateTime timestamp = DateTime.UtcNow;

            if (message != null)
            {
                // Parse RegisterMessage from received message
                var regCollection = message.InteractionElements?
                    .OfType<SubmodelElementCollection>()
                    .FirstOrDefault(c => string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase));

                if (regCollection != null)
                {
                    registerMsg = RegisterMessage.FromSubmodelElementCollection(regCollection);
                    agentId = registerMsg.AgentId;
                    timestamp = registerMsg.Timestamp;
                }

                if (string.IsNullOrWhiteSpace(agentId))
                {
                    agentId = message.Frame?.Sender?.Identification?.Id ?? string.Empty;
                }

                role = message.Frame?.Sender?.Role?.Name ?? string.Empty;
            }

            // If no message or no RegisterMessage, build from context (initial registration)
            if (registerMsg == null || string.IsNullOrWhiteSpace(agentId))
            {
                agentId = Context.Get<string>("AgentId")
                       ?? Context.Get<string>("config.Agent.AgentId")
                       ?? Context.AgentId
                       ?? string.Empty;

                role = Context.Get<string>("AgentRole")
                    ?? Context.Get<string>("config.Agent.Role")
                    ?? Context.AgentRole
                    ?? string.Empty;

                // Align with RegisterAgentNode: avoid sub-holons syncing under the bare ModuleId.
                agentId = ResolveRegistrationAgentId(agentId, role);

                // Build RegisterMessage from context
                var subagents = new List<string>();
                var capabilities = new List<string>();

                // Try to get subagents from DispatchingState
                try
                {
                    var dispatchState = Context.Get<DispatchingState>("DispatchingState");
                    if (dispatchState != null)
                    {
                        subagents = dispatchState.AllModuleIds().ToList();
                    }
                }
                catch { }

                registerMsg = new RegisterMessage(agentId, subagents, capabilities);
            }

            if (string.IsNullOrWhiteSpace(agentId))
            {
                Logger.LogWarning("SyncAgentToNeo4j: Could not determine agentId from message or context");
                return NodeStatus.Failure;
            }
            var agentType = DetermineAgentType(role);

            var ns = Context.Get<string>("config.Namespace")
                  ?? Context.Get<string>("Namespace")
                  ?? "unknown";

            // Namespace in Neo4j has underscore prefix (e.g. "_PHUKET")
            var namespaceValue = ns.StartsWith("_") ? ns : $"_{ns.ToUpper()}";

            // Determine parent agent
            var parentAgentId = DetermineParentAgent(role, agentId, ns);

            // Use timestamp from registerMsg if available, otherwise current time
            if (registerMsg.Timestamp == default)
            {
                registerMsg = new RegisterMessage(agentId, registerMsg.Subagents, registerMsg.Capabilities);
            }
            timestamp = registerMsg.Timestamp;

            await using var session = driver.AsyncSession(o => o.WithDatabase(database));

            // Cypher: MERGE Agent, IS_SUBAGENT_OF, MANAGES_ASSET
            // Set agentType as label dynamically (e.g. :ModuleHolon, :PlanningHolon)
            var query = $@"
                MERGE (a:Agent {{agentId: $agentId}})
REMOVE a:DispatchingAgent:ModuleHolon:PlanningHolon:ExecutionHolon:ProductAgent
                SET 
                a:{agentType},
                a.agentType = $agentType,
                a.namespace = $namespace,
                a.lastRegistration = datetime($timestamp),
                a.lastSeen = datetime($timestamp),
                a.isActive = true,
                a.capabilities = $capabilities,
                a.subagents = $subagents

                WITH a
                CALL {{
                    WITH a
        WITH a, $parentAgentId AS pid
        WHERE pid IS NOT NULL AND pid <> '' AND pid <> $agentId
        MERGE (parent:Agent {{agentId: pid}})
        MERGE (a)-[r:IS_SUBAGENT_OF]->(parent)
                    SET r.registeredAt = datetime($timestamp)
                    RETURN count(*) AS parentLinked
                }}

                WITH a
                CALL {{
                    WITH a
                    OPTIONAL MATCH (asset:Asset {{shell_id: $agentId}})
                    WHERE $agentType = 'ModuleHolon' AND asset IS NOT NULL
                    WITH a, asset
                    WHERE asset IS NOT NULL
                    MERGE (a)-[m:MANAGES_ASSET]->(asset)
                    SET m.since = datetime($timestamp)
                    RETURN count(*) AS assetLinked
                }}

                WITH a
                CALL {{
                    WITH a
                    OPTIONAL MATCH (ns:Namespace {{value: $namespace}})
                    WHERE $agentType = 'DispatchingAgent' AND ns IS NOT NULL
                    WITH a, ns
                    WHERE ns IS NOT NULL
                    MERGE (a)-[r:MANAGES_NAMESPACE]->(ns)
                    SET r.since = datetime($timestamp)
                    RETURN count(*) AS namespaceLinked
                }}

                RETURN a.agentId AS agentId
                ";

            var parameters = new
            {
                agentId = agentId,
                agentType = agentType,
                @namespace = namespaceValue,  // Use namespaceValue with underscore prefix
                timestamp = timestamp.ToString("o"),
                capabilities = registerMsg.Capabilities.ToArray(),
                subagents = registerMsg.Subagents.ToArray(),
                parentAgentId = parentAgentId ?? string.Empty
            };

            var cursor = await session.RunAsync(query, parameters);
            var result = await cursor.SingleOrDefaultAsync();

            if (result != null)
            {
                Logger.LogDebug(
                    "SyncAgentToNeo4j: Synced agent {AgentId} (type={Type}, parent={Parent})",
                    agentId,
                    agentType,
                    parentAgentId ?? "none");
                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning("SyncAgentToNeo4j: Query returned no result for {AgentId}", agentId);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SyncAgentToNeo4j: Exception during Neo4j sync");
            return NodeStatus.Failure;
        }
    }

    private string DetermineAgentType(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "Agent";
        }

        role = role.ToLowerInvariant();

        if (role.Contains("planning"))
            return "PlanningHolon";
        if (role.Contains("execution"))
            return "ExecutionHolon";
        if (role.Contains("module"))
            return "ModuleHolon";
        if (role.Contains("dispatching"))
            return "DispatchingAgent";
        if (role.Contains("product"))
            return "ProductAgent";

        return "Agent";
    }

    private string ResolveRegistrationAgentId(string currentAgentId, string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return currentAgentId;
        }

        var lower = role.ToLowerInvariant();
        var looksLikeSubHolon = lower.Contains("planning") || lower.Contains("execution") || lower.Contains("subholon");
        if (!looksLikeSubHolon)
        {
            return currentAgentId;
        }

        var moduleId = Context.Get<string>("config.Agent.ModuleId")
                    ?? Context.Get<string>("ModuleId")
                    ?? string.Empty;

        if (string.IsNullOrWhiteSpace(moduleId))
        {
            return currentAgentId;
        }

        if (!string.IsNullOrWhiteSpace(currentAgentId)
            && currentAgentId.StartsWith(moduleId + "_", StringComparison.OrdinalIgnoreCase))
        {
            return currentAgentId;
        }

        var rolePart = role.Trim().Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
        return $"{moduleId}_{rolePart}";
    }

    private string? DetermineParentAgent(string role, string agentId, string ns)
    {
        // Try explicit config
        var parentFromConfig = Context.Get<string>("config.Agent.ParentAgent")
                            ?? Context.Get<string>("config.Agent.ParentId")
                            ?? Context.Get<string>("config.Agent.ParentModuleId");

        if (!string.IsNullOrWhiteSpace(parentFromConfig))
        {
            // Normalize namespace suffix from parent
            if (parentFromConfig.EndsWith($"_{ns}", StringComparison.OrdinalIgnoreCase))
            {
                parentFromConfig = parentFromConfig.Substring(0, parentFromConfig.Length - ns.Length - 1);
            }
            return parentFromConfig;
        }

        // Role-based fallback
        role = (role ?? string.Empty).ToLowerInvariant();
        var contextRole = (Context.AgentRole ?? Context.Get<string>("AgentRole") ?? string.Empty).ToLowerInvariant();
        var contextAgentId = Context.Get<string>("AgentId")
                            ?? Context.Get<string>("config.Agent.AgentId")
                            ?? Context.AgentId
                            ?? string.Empty;

        if (role.Contains("planning") || role.Contains("execution"))
        {
            // Sub-Holon → parent is ModuleId
            var moduleId = Context.Get<string>("config.Agent.ModuleId")
                        ?? Context.Get<string>("ModuleId");

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                moduleId = ModuleContextHelper.ResolveModuleId(Context);
            }

            if (string.IsNullOrWhiteSpace(moduleId))
            {
                moduleId = TryDeriveModuleIdFromAgentId(agentId);
            }

            if (string.IsNullOrWhiteSpace(moduleId) && contextRole.Contains("module") && !string.IsNullOrWhiteSpace(contextAgentId))
            {
                moduleId = contextAgentId;
            }

            if (!string.IsNullOrWhiteSpace(moduleId)
                && string.Equals(moduleId, agentId, StringComparison.OrdinalIgnoreCase))
            {
                // Prevent self-loop relationships like (P100)-[:IS_SUBAGENT_OF]->(P100)
                return null;
            }

            return moduleId;
        }

        if (role.Contains("module"))
        {
            // ModuleHolon → parent is DispatchingAgent
            if (!string.IsNullOrWhiteSpace(contextAgentId) && contextRole.Contains("dispatching"))
            {
                return contextAgentId;
            }
            return "DispatchingAgent";
        }

        // When running inside a dispatcher context and syncing another agent, treat dispatcher as parent.
        if (!string.IsNullOrWhiteSpace(contextAgentId)
            && !string.Equals(agentId, contextAgentId, StringComparison.OrdinalIgnoreCase)
            && contextRole.Contains("dispatching"))
        {
            return contextAgentId;
        }

        // DispatchingAgent has no parent (top-level)
        return null;
    }

    private static string? TryDeriveModuleIdFromAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        // Common conventions: P102_Planning, P102_Execution, etc.
        var idx = agentId.IndexOf('_');
        if (idx > 0)
        {
            return agentId.Substring(0, idx);
        }

        return null;
    }
}

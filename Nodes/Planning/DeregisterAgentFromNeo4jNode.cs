using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// Deregistriert einen Agenten in Neo4j (setzt isActive=false).
/// Optional: Löscht den Node nach einer Grace-Period.
/// </summary>
public class DeregisterAgentFromNeo4jNode : BTNode
{
    /// <summary>
    /// Agent-ID, die deregistriert werden soll (kann über XML-Attribut oder Blackboard gesetzt werden).
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Wenn true, wird der Agent-Node komplett gelöscht (DETACH DELETE).
    /// Wenn false, wird nur isActive=false gesetzt.
    /// </summary>
    public bool DeleteNode { get; set; } = false;

    public DeregisterAgentFromNeo4jNode() : base("DeregisterAgentFromNeo4j") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var driver = Context.Get<IDriver>("Neo4jDriver");
            if (driver == null)
            {
                Logger.LogWarning("DeregisterAgentFromNeo4j: Neo4jDriver not available in context");
                return NodeStatus.Failure;
            }

            var database = Context.Get<string>("config.Neo4j.Database")
                         ?? Environment.GetEnvironmentVariable("NEO4J_DATABASE")
                         ?? "neo4j";

            // Resolve AgentId (XML attribute oder Blackboard)
            var agentId = ResolveTemplates(AgentId);
            if (string.IsNullOrWhiteSpace(agentId))
            {
                agentId = Context.Get<string>("AgentId")
                       ?? Context.Get<string>("config.Agent.AgentId")
                       ?? Context.AgentId;
            }

            if (string.IsNullOrWhiteSpace(agentId))
            {
                Logger.LogWarning("DeregisterAgentFromNeo4j: No AgentId specified");
                return NodeStatus.Failure;
            }

            await using var session = driver.AsyncSession(o => o.WithDatabase(database));

            var timestamp = DateTime.UtcNow;

            string query;
            if (DeleteNode)
            {
                // Vollständiges Löschen (inkl. Relationships)
                query = @"
MATCH (a:Agent {agentId: $agentId})
DETACH DELETE a
RETURN count(a) AS deleted
";
            }
            else
            {
                // Nur isActive=false setzen
                query = @"
MATCH (a:Agent {agentId: $agentId})
SET a.isActive = false, a.lastSeen = datetime($timestamp)
RETURN a.agentId AS agentId
";
            }

            var parameters = new
            {
                agentId = agentId,
                timestamp = timestamp.ToString("o")
            };

            var cursor = await session.RunAsync(query, parameters);
            var result = await cursor.SingleOrDefaultAsync();

            if (result != null)
            {
                if (DeleteNode)
                {
                    var deleted = result["deleted"].As<int>();
                    Logger.LogInformation(
                        "DeregisterAgentFromNeo4j: Deleted agent {AgentId} from Neo4j (count={Count})",
                        agentId,
                        deleted);
                }
                else
                {
                    Logger.LogInformation(
                        "DeregisterAgentFromNeo4j: Deregistered agent {AgentId} (isActive=false)",
                        agentId);
                }

                return NodeStatus.Success;
            }
            else
            {
                Logger.LogWarning(
                    "DeregisterAgentFromNeo4j: Agent {AgentId} not found in Neo4j",
                    agentId);
                return NodeStatus.Failure;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DeregisterAgentFromNeo4j: Exception during Neo4j deregistration");
            return NodeStatus.Failure;
        }
    }

    private string ResolveTemplates(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('{'))
        {
            return value;
        }

        var result = value;
        var startIndex = 0;
        while (startIndex < result.Length)
        {
            var openBrace = result.IndexOf('{', startIndex);
            if (openBrace == -1) break;

            var closeBrace = result.IndexOf('}', openBrace);
            if (closeBrace == -1) break;

            var placeholder = result.Substring(openBrace + 1, closeBrace - openBrace - 1);
            var replacement = Context.Get<string>(placeholder) ?? $"{{{placeholder}}}";
            result = result.Substring(0, openBrace) + replacement + result.Substring(closeBrace + 1);
            startIndex = openBrace + replacement.Length;
        }

        return result;
    }
}

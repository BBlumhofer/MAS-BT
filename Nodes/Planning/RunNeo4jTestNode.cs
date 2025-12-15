using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using Neo4j.Driver;

namespace MAS_BT.Nodes.Planning;

public class RunNeo4jTestNode : BTNode
{
    public RunNeo4jTestNode() : base("RunNeo4jTest") { }

    public override async Task<NodeStatus> Execute()
    {
        try
        {
            var uri = Context.Get<string>("config.Neo4j.Uri") ?? Environment.GetEnvironmentVariable("NEO4J_URI") ?? "neo4j://localhost:7687";
            var user = Context.Get<string>("config.Neo4j.Username") ?? Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";
            var password = Context.Get<string>("config.Neo4j.Password") ?? Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "neo4j";
            var database = Context.Get<string>("config.Neo4j.Database") ?? Environment.GetEnvironmentVariable("NEO4J_DATABASE") ?? "neo4j";

            Logger.LogInformation("RunNeo4jTest: Connecting to {Uri} (DB={Db})", uri, database);

            IDriver? driver = null;
            var createdLocalDriver = false;
            try
            {
                // Try reuse driver from context
                driver = Context.Get<IDriver>("Neo4jDriver");
            }
            catch { driver = null; }

            if (driver == null)
            {
                driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
                createdLocalDriver = true;
            }

            await using var session = driver.AsyncSession(o => o.WithDatabase(database));

            // 1) CALL db.labels()
            Logger.LogInformation("RunNeo4jTest: Executing CALL db.labels()");
            try
            {
                var cursor = await session.RunAsync("CALL db.labels()");
                var records = await cursor.ToListAsync();
                var labels = records.Select(r => r[0]?.ToString() ?? string.Empty).ToList();
                Logger.LogInformation("RunNeo4jTest: Labels ({Count}): {Labels}", labels.Count, string.Join(", ", labels));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "RunNeo4jTest: db.labels() query failed");
            }

            // 2) Count submodels and shells in a single query
            Logger.LogInformation("RunNeo4jTest: Counting Submodels and Shells");
            try
            {
                var q = @"CALL { MATCH (n:Submodel) RETURN count(n) AS submodels }
CALL { MATCH (m:Shell) RETURN count(m) AS shells }
RETURN submodels, shells";
                var cursor2 = await session.RunAsync(q);
                var rec = await cursor2.SingleAsync();
                var submodels = rec["submodels"].As<long>();
                var shells = rec["shells"].As<long>();
                Logger.LogInformation("RunNeo4jTest: submodels={Sub}, shells={Shells}", submodels, shells);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "RunNeo4jTest: count query failed");
            }

            // 3) Fetch capability reference as stored in the graph (c.Reference)
            var moduleShellId = Context.Get<string>("config.Agent.ModuleId")
                                ?? Context.Get<string>("ModuleId")
                                ?? Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_MODULE")
                                ?? "P102";
            var capabilityIdShort = Context.Get<string>("config.Neo4j.TestCapability")
                                   ?? Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_CAPABILITY")
                                   ?? "Assemble";

            Logger.LogInformation(
                "RunNeo4jTest: Querying capability reference (moduleShellId={Module}, capabilityIdShort={Capability})",
                moduleShellId,
                capabilityIdShort);

            try
            {
                var qRef = @"MATCH p = (a:Asset)-[r:PROVIDES_CAPABILITY]->(c:Capability)
WHERE a.shell_id = $moduleShellId AND c.idShort = $capabilityIdShort
Return c.Reference as Reference";

                var cursor3 = await session.RunAsync(qRef, new { moduleShellId = moduleShellId, capabilityIdShort = capabilityIdShort });
                var rec3 = await cursor3.SingleOrDefaultAsync();
                if (rec3 != null && rec3.Keys.Contains("Reference"))
                {
                    var reference = rec3["Reference"]?.ToString() ?? string.Empty;
                    Logger.LogInformation("RunNeo4jTest: Capability reference: {Reference}", reference);
                }
                else
                {
                    Logger.LogWarning("RunNeo4jTest: Capability reference query returned no records");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "RunNeo4jTest: reference query failed");
            }

            if (createdLocalDriver && driver is not null)
            {
                await driver.DisposeAsync();
            }

            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RunNeo4jTest: Exception");
            return NodeStatus.Failure;
        }
    }
}

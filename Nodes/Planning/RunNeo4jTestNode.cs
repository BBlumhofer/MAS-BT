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
            var moduleShellId = Context.Get<string>("config.Neo4jTestInfos.Asset")
                                ?? Context.Get<string>("config.Agent.ModuleId")
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

            // 4) Fetch module position (if present)
            Logger.LogInformation("RunNeo4jTest: Querying module position for {Module}", moduleShellId);
            try
            {
                var qPos = @"MATCH (n:Asset {shell_id: $moduleShellId})-[:HAS_POSITION]->(p:Position)
RETURN
    n.shell_id AS Name,
    p.X AS X_Position,
    p.Y AS Y_Position";

                var cursor4 = await session.RunAsync(qPos, new { moduleShellId = moduleShellId });
                var rec4 = await cursor4.SingleOrDefaultAsync();
                if (rec4 != null && rec4.Keys.Contains("X_Position") && rec4.Keys.Contains("Y_Position"))
                {
                    var xStr = rec4["X_Position"]?.ToString() ?? string.Empty;
                    var yStr = rec4["Y_Position"]?.ToString() ?? string.Empty;
                    Logger.LogInformation("RunNeo4jTest: Module position for {Module} -> X={X}, Y={Y}", moduleShellId, xStr, yStr);

                    // store in blackboard for other nodes/tests
                    Context.Set("ModulePosition.X", xStr);
                    Context.Set("ModulePosition.Y", yStr);
                }
                else
                {
                    Logger.LogWarning("RunNeo4jTest: Position query returned no records for module {Module}", moduleShellId);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "RunNeo4jTest: position query failed");
            }

            // 5) Query current storage slot contents per Agent
            Logger.LogInformation("RunNeo4jTest: Querying storage slot contents for all agents");
            try
            {
                var qSlots = @"MATCH (n:Agent)-[r:HAS_STORAGE]->(s:Storage)-[l:HAS_SLOT]->(sl:Slot)
RETURN n.agentId AS ID, sl.productId AS ProductID, sl.productType as ProductType, sl.carrierId as CarrierID, sl.carrierType as CarrierType, sl.isEmpty as IsEmpty";

                var cursor5 = await session.RunAsync(qSlots);
                var records = await cursor5.ToListAsync();
                Logger.LogInformation("RunNeo4jTest: Found {Count} slot entries", records.Count);

                foreach (var r in records)
                {
                    var id = r["ID"]?.ToString() ?? string.Empty;
                    var productId = r["ProductID"]?.ToString() ?? string.Empty;
                    var productType = r["ProductType"]?.ToString() ?? string.Empty;
                    var carrierId = r["CarrierID"]?.ToString() ?? string.Empty;
                    var carrierType = r["CarrierType"]?.ToString() ?? string.Empty;
                    var isEmpty = r["IsEmpty"]?.ToString() ?? string.Empty;

                    Logger.LogInformation("RunNeo4jTest: SlotRow -> Agent={Agent}, ProductID={ProductID}, ProductType={ProductType}, CarrierID={CarrierID}, CarrierType={CarrierType}, IsEmpty={IsEmpty}",
                        id, productId, productType, carrierId, carrierType, isEmpty);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "RunNeo4jTest: slot query failed");
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

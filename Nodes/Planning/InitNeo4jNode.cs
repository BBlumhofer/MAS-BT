using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using Neo4j.Driver;
using MAS_BT.Services.Graph;

namespace MAS_BT.Nodes.Planning;

public class InitNeo4jNode : BTNode
{
    public InitNeo4jNode() : base("InitNeo4j") { }

    public override async Task<NodeStatus> Execute()
    {
        const int retryCount = 3;
        var retryDelay = TimeSpan.FromMilliseconds(1000);
        try
        {
            // Read config values (will be set by ReadConfig node as config.* keys)
            var uri = Context.Get<string>("config.Neo4j.Uri") ?? Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687";
            var user = Context.Get<string>("config.Neo4j.Username") ?? Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";
            var password = Context.Get<string>("config.Neo4j.Password") ?? Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "neo4j";
            var database = Context.Get<string>("config.Neo4j.Database") ?? Environment.GetEnvironmentVariable("NEO4J_DATABASE") ?? "neo4j";

            Logger.LogInformation("InitNeo4j: Connecting to Neo4j at {Uri} (DB={Db})", uri, database);

            var auth = AuthTokens.Basic(user, password);

            IDriver driver;
            string effectiveUri;
            (driver, effectiveUri) = await TryConnectWithRetriesAsync(uri, auth, database, retryCount, retryDelay);

            
                        // Ensure basic schema constraints so MERGE works deterministically and duplicates are prevented.
                        // If the DB already contains duplicates, constraint creation can fail; we log and continue.
                        try
                        {
                            await using var schemaSession = driver.AsyncSession(o => o.WithDatabase(database));

                            // Agent identity must be unique.
                            await schemaSession.RunAsync(
                                "CREATE CONSTRAINT agent_agentId_unique IF NOT EXISTS FOR (a:Agent) REQUIRE a.agentId IS UNIQUE");

                            // Optional: Namespace identity should be unique when used.
                            await schemaSession.RunAsync(
                                "CREATE CONSTRAINT namespace_value_unique IF NOT EXISTS FOR (n:Namespace) REQUIRE n.value IS UNIQUE");

                            // Inventory graph nodes should also be unique to prevent duplicates.
                            await schemaSession.RunAsync(
                                "CREATE CONSTRAINT storage_storageId_unique IF NOT EXISTS FOR (s:Storage) REQUIRE s.storageId IS UNIQUE");

                            await schemaSession.RunAsync(
                                "CREATE CONSTRAINT slot_slotId_unique IF NOT EXISTS FOR (sl:Slot) REQUIRE sl.slotId IS UNIQUE");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "InitNeo4j: Failed to create Neo4j schema constraints (continuing without constraints). If duplicates already exist, clean them up before enabling UNIQUE constraints.");
                        }

            // store driver and capability query implementation in blackboard for other nodes
            Context.Set("Neo4jDriver", driver);
            Context.Set("GraphCapabilityQuery", new Neo4jCapabilityQuery(driver, database));
            Context.Set("CapabilityPropertyQuery", new Neo4jCapabilityPropertyQuery(driver, database));
            Context.Set("CapabilityReferenceQuery", new Neo4jCapabilityReferenceQuery(driver, database));

            Logger.LogInformation("InitNeo4j: Neo4j client initialized (Uri={Uri})", effectiveUri);
            return NodeStatus.Success;
        }
        catch (Neo4j.Driver.AuthenticationException authEx)
        {
            var user = Context.Get<string>("config.Neo4j.Username") ?? Environment.GetEnvironmentVariable("NEO4J_USER");
            var uri = Context.Get<string>("config.Neo4j.Uri") ?? Environment.GetEnvironmentVariable("NEO4J_URI");
            throw new InvalidOperationException($"InitNeo4j: Authentication failed when connecting to Neo4j at {uri} as user {user}. Verify config.Neo4j.Username/config.Neo4j.Password or env NEO4J_USER/NEO4J_PASSWORD.", authEx);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("InitNeo4j: Failed to initialize Neo4j client", ex);
        }
    }

    private static bool ShouldTryBoltFallback(string uri, ServiceUnavailableException ex)
    {
        if (!IsSingleHostNeo4jUri(uri))
        {
            return false;
        }

        // Typical message: "Failed to connect to any routing server"
        return ex.Message.Contains("routing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSingleHostNeo4jUri(string uri)
    {
        if (!uri.StartsWith("neo4j://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // neo4j://host1,host2:7687 indicates multi-host routing.
        return !uri.Contains(',', StringComparison.Ordinal);
    }

    private static string ToBoltUri(string uri)
    {
        if (!uri.StartsWith("neo4j://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return "bolt://" + uri.Substring("neo4j://".Length);
    }

    private static async Task<(IDriver driver, string effectiveUri)> CreateAndVerifyDriverAsync(
        string uri,
        IAuthToken auth,
        string database)
    {
        var driver = GraphDatabase.Driver(uri, auth);
        try
        {
            await using var session = driver.AsyncSession(o => o.WithDatabase(database));
            var cursor = await session.RunAsync("RETURN 1 AS ok");
            await cursor.FetchAsync();
            return (driver, uri);
        }
        catch
        {
            driver.Dispose();
            throw;
        }
    }

    private async Task<(IDriver driver, string effectiveUri)> TryConnectWithRetriesAsync(
        string uri,
        IAuthToken auth,
        string database,
        int retryCount,
        TimeSpan retryDelay)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                return await ConnectOnceAsync(uri, auth, database).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= retryCount)
                {
                    break;
                }

                Logger.LogWarning(
                    ex,
                    "InitNeo4j: connection attempt {Attempt}/{Total} failed. Retrying in {Delay} ms",
                    attempt,
                    retryCount,
                    retryDelay.TotalMilliseconds);

                try
                {
                    await Task.Delay(retryDelay).ConfigureAwait(false);
                }
                catch
                {
                    // ignore cancellation for retry delays
                }
            }
        }

        throw lastError ?? new InvalidOperationException("InitNeo4j: connection attempts exhausted");
    }

    private async Task<(IDriver driver, string effectiveUri)> ConnectOnceAsync(string uri, IAuthToken auth, string database)
    {
        try
        {
            return await CreateAndVerifyDriverAsync(uri, auth, database).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException ex) when (ShouldTryBoltFallback(uri, ex))
        {
            var boltUri = ToBoltUri(uri);
            Logger.LogWarning(
                ex,
                "InitNeo4j: Connectivity check failed for routing URI {Uri}. Retrying with {BoltUri}",
                uri,
                boltUri);
            return await CreateAndVerifyDriverAsync(boltUri, auth, database).ConfigureAwait(false);
        }
    }

}

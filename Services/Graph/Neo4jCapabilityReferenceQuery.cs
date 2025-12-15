using System;
using System.Threading;
using System.Threading.Tasks;
using Neo4j.Driver;

namespace MAS_BT.Services.Graph;

public interface ICapabilityReferenceQuery
{
    Task<string?> GetCapabilityReferenceJsonAsync(
        string moduleShellId,
        string capabilityIdShort,
        CancellationToken cancellationToken = default);
}

public sealed class Neo4jCapabilityReferenceQuery : ICapabilityReferenceQuery
{
    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jCapabilityReferenceQuery(IDriver driver, string database = "neo4j")
    {
        _driver = driver;
        _database = database;
    }

    public async Task<string?> GetCapabilityReferenceJsonAsync(
        string moduleShellId,
        string capabilityIdShort,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleShellId))
        {
            throw new ArgumentException("Module shell id missing", nameof(moduleShellId));
        }

        if (string.IsNullOrWhiteSpace(capabilityIdShort))
        {
            throw new ArgumentException("Capability idShort missing", nameof(capabilityIdShort));
        }

        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        var query = @"
MATCH p = (a:Asset)-[:PROVIDES_CAPABILITY]->(c:Capability)
WHERE a.shell_id = $moduleShellId AND c.idShort = $capabilityIdShort
RETURN c.Reference as Reference
LIMIT 1";

        var cursor = await session.RunAsync(query, new { moduleShellId, capabilityIdShort });

        IRecord? record;
        try
        {
            record = await cursor.SingleOrDefaultAsync(cancellationToken);
        }
        catch
        {
            // Some neo4j driver versions do not support cancellationToken overload.
            record = await cursor.SingleOrDefaultAsync();
        }

        if (record == null)
        {
            return null;
        }

        if (!record.Keys.Contains("Reference"))
        {
            return null;
        }

        var value = record["Reference"];
        if (value == null)
        {
            return null;
        }

        try
        {
            var s = value.As<string>();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            var s = value.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}

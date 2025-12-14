using System;
using System.Threading;
using System.Threading.Tasks;
using MAS_BT.Nodes.Planning;
using MAS_BT.Services.Graph;
using Neo4j.Driver;
using Xunit;

namespace MAS_BT.Tests;

public class Neo4jCapabilityPropertyQueryTests
{
    [Fact]
    public void TryParseEmbedding_ParsesCsvDoubles()
    {
        var parsed = Neo4jCapabilityPropertyQuery.TryParseEmbedding("1.0,-2, 3.5");
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Length);
        Assert.Equal(1.0, parsed[0], 6);
        Assert.Equal(-2.0, parsed[1], 6);
        Assert.Equal(3.5, parsed[2], 6);
    }

    [Fact]
    public async Task Descriptor_UsesStoredEmbeddingWithoutCallingProvider()
    {
        var stored = new[] { 0.1, 0.2, 0.3 };
        var container = new GraphCapabilityPropertyContainer(
            IdShort: "RoleOfContactPerson",
            SemanticId: "0173-1#07-AAS927#001",
            ValueType: "xs:string",
            Value: "0173-1#07-AAS931#001",
            Min: null,
            Max: null,
            ListValues: Array.Empty<string>(),
            Embedding: stored);

        var descriptor = CapabilityPropertyDescriptor.TryCreate(container);
        Assert.NotNull(descriptor);

        var embedding = await descriptor!.GetEmbeddingAsync(new ThrowingEmbeddingProvider(), CancellationToken.None);
        Assert.NotNull(embedding);
        Assert.Equal(stored, embedding!);
    }

    [Fact]
    public async Task Integration_Neo4jQuery_ReturnsContainers_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var uri = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_URI") ?? "neo4j://192.168.178.30:7687";
        var user = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_USER") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_PASSWORD") ?? "testtest";
        var database = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_DATABASE") ?? "neo4j";
        var moduleId = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_MODULE") ?? "P17";
        var capability = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_CAPABILITY") ?? "Soldering";

        using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        var query = new Neo4jCapabilityPropertyQuery(driver, database);

        var containers = await query.GetCapabilityPropertyContainersAsync(moduleId, capability, CancellationToken.None);
        Assert.NotNull(containers);
        Assert.NotEmpty(containers);
    }

    private sealed class ThrowingEmbeddingProvider : MAS_BT.Services.Embeddings.ITextEmbeddingProvider
    {
        public Task<double[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Embedding provider should not be called when embedding is already present");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using BaSyx.Models.Extensions;
using MAS_BT.Nodes.Planning;
using MAS_BT.Services.Graph;
using Neo4j.Driver;
using Xunit;
using Xunit.Abstractions;

namespace MAS_BT.Tests;

public class Neo4jCapabilityPropertyQueryTests
{
    private readonly ITestOutputHelper _output;

    public Neo4jCapabilityPropertyQueryTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

    [Fact]
    public async Task Integration_Neo4jQuery_ReturnsCapabilityReference_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var uri = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_URI") ?? "neo4j://192.168.178.30:7687";
        var user = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_USER") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_PASSWORD") ?? "testtest";
        var database = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_DATABASE") ?? "neo4j";
        var moduleId = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_MODULE") ?? "P102";
        var capability = Environment.GetEnvironmentVariable("MASBT_TEST_NEO4J_CAPABILITY") ?? "Assemble";

        using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        var query = new Neo4jCapabilityReferenceQuery(driver, database);

        var referenceJson = await query.GetCapabilityReferenceJsonAsync(moduleId, capability, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(referenceJson));

        // Convert the returned JSON array (keys) into a real BaSyx ReferenceElement.
        var keysDto = JsonSerializer.Deserialize<List<ReferenceKeyDto>>(
            referenceJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(keysDto);
        Assert.NotEmpty(keysDto!);

        var keys = keysDto!
            .Where(k => !string.IsNullOrWhiteSpace(k.Type) && !string.IsNullOrWhiteSpace(k.Value))
            .Select(k =>
            {
                if (!Enum.TryParse<KeyType>(k.Type!, ignoreCase: true, out var kt))
                {
                    kt = KeyType.Undefined;
                }
                return (IKey)new Key(kt, k.Value!.Trim());
            })
            .ToList();

        Assert.NotEmpty(keys);

        var basyxReference = new Reference(keys)
        {
            Type = ReferenceType.ModelReference
        };

        var referenceElement = new ReferenceElement("Reference")
        {
            Value = new ReferenceElementValue(basyxReference)
        };

        var basyxJson = JsonSerializer.Serialize<ISubmodelElement>(referenceElement, CreateBasyxOptions());
        _output.WriteLine("Neo4j c.Reference raw: {0}", referenceJson);
        _output.WriteLine("As BaSyx ReferenceElement JSON: {0}", basyxJson);

        var outPath = ResolveTestFilePath("neo4j_reference_element.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, basyxJson, CancellationToken.None);
        _output.WriteLine("Wrote BaSyx ReferenceElement JSON to: {0}", outPath);

        Assert.Contains("Reference", basyxJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keys", basyxJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTestFilePath(string fileName)
    {
        // Goal: write into MAS-BT/tests/TestFiles/<fileName> regardless of whether the test
        // is invoked from repo root or from within MAS-BT/.
        var cwd = Environment.CurrentDirectory;

        string masBtRoot;
        if (Directory.Exists(Path.Combine(cwd, "MAS-BT"))
            && Directory.Exists(Path.Combine(cwd, "MAS-BT", "tests")))
        {
            masBtRoot = Path.Combine(cwd, "MAS-BT");
        }
        else
        {
            masBtRoot = cwd;
        }

        return Path.Combine(masBtRoot, "tests", "TestFiles", fileName);
    }

    private sealed class ReferenceKeyDto
    {
        public string? Type { get; set; }
        public string? Value { get; set; }
    }

    private static JsonSerializerOptions CreateBasyxOptions()
    {
        return new JsonSerializerOptions
        {
            Converters =
            {
                new FullSubmodelElementConverter(new ConverterOptions()),
                new ReferenceJsonConverter(),
                new JsonStringEnumConverter()
            },
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
    }

    private sealed class ThrowingEmbeddingProvider : MAS_BT.Services.Embeddings.ITextEmbeddingProvider
    {
        public Task<double[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Embedding provider should not be called when embedding is already present");
        }
    }
}

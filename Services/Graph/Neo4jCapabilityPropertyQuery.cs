using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo4j.Driver;

namespace MAS_BT.Services.Graph;

public interface ICapabilityPropertyQuery
{
    Task<IReadOnlyList<GraphCapabilityPropertyContainer>> GetCapabilityPropertyContainersAsync(
        string moduleShellId,
        string capabilityIdShort,
        CancellationToken cancellationToken = default);
}

public sealed record GraphCapabilityPropertyContainer(
    string IdShort,
    string? SemanticId,
    string? ValueType,
    string? Value,
    double? Min,
    double? Max,
    IReadOnlyList<string> ListValues,
    double[]? Embedding);

public sealed class Neo4jCapabilityPropertyQuery : ICapabilityPropertyQuery
{
    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jCapabilityPropertyQuery(IDriver driver, string database = "neo4j")
    {
        _driver = driver;
        _database = database;
    }

    public async Task<IReadOnlyList<GraphCapabilityPropertyContainer>> GetCapabilityPropertyContainersAsync(
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

        // The graph stores the value/range/list payload in sub-properties below the container.
        // We therefore fetch the container node and all reachable child nodes up to a small depth.
        var query = @"
MATCH (a:Asset)-[:PROVIDES_CAPABILITY]->(cap:Capability)-[:HAS_PROPERTY]->(p:CapabilityPropertyContainer)
WHERE a.shell_id = $moduleShellId AND cap.idShort = $capabilityIdShort
OPTIONAL MATCH (p)-[*1..5]->(child)
RETURN p AS container, collect(DISTINCT child) AS children";

        var cursor = await session.RunAsync(
            query,
            new { moduleShellId, capabilityIdShort });

        var result = new List<GraphCapabilityPropertyContainer>();
        while (await cursor.FetchAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var container = cursor.Current["container"].As<INode>();
            var children = cursor.Current["children"].As<List<INode>>() ?? new List<INode>();
            result.Add(MapContainer(container, children));
        }

        return result;
    }

    private static GraphCapabilityPropertyContainer MapContainer(INode container, IReadOnlyCollection<INode> children)
    {
        var idShort = GetString(container, "idShort") ?? GetString(container, "IdShort") ?? string.Empty;
        var semanticId = GetString(container, "semanticId") ?? GetString(container, "SemanticId");
        var valueType = GetString(container, "valueType") ?? GetString(container, "ValueType");

        // Embedding is stored as comma-separated float list (string)
        var embedding = TryParseEmbedding(GetString(container, "embedding") ?? GetString(container, "Embedding"));

        // Some graphs might duplicate the payload on the container itself.
        var value = GetString(container, "value") ?? GetString(container, "Value");
        var min = TryParseDouble(GetString(container, "min") ?? GetString(container, "Min"));
        var max = TryParseDouble(GetString(container, "max") ?? GetString(container, "Max"));
        var listValues = TryParseStringList(container.Properties.TryGetValue("listValues", out var listObj) ? listObj : null);

        if (value == null && !min.HasValue && !max.HasValue && listValues.Count == 0)
        {
            // Fallback: infer payload from children.
            (value, min, max, listValues) = InferPayloadFromChildren(idShort, children);
        }

        return new GraphCapabilityPropertyContainer(
            IdShort: idShort,
            SemanticId: semanticId,
            ValueType: valueType,
            Value: value,
            Min: min,
            Max: max,
            ListValues: listValues,
            Embedding: embedding);
    }

    private static (string? value, double? min, double? max, IReadOnlyList<string> listValues) InferPayloadFromChildren(
        string containerIdShort,
        IReadOnlyCollection<INode> children)
    {
        var baseName = NormalizeContainerName(containerIdShort);

        // Heuristics:
        // - Range nodes often carry min/max, or min/max are properties on child nodes.
        // - Lists are represented by a list container node with nested property elements holding values.
        // - Values are represented by a property node with a 'value' field.

        // Range via explicit min/max properties
        var min = TryParseDouble(FindValueInChildren(children, "min"));
        var max = TryParseDouble(FindValueInChildren(children, "max"));
        if (min.HasValue || max.HasValue)
        {
            return (null, min, max, Array.Empty<string>());
        }

        // Prefer a direct child whose idShort matches the container base name (e.g. GripForceContainer -> child GripForce)
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            var direct = FindValueInChildren(children, baseName);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return (direct, null, null, Array.Empty<string>());
            }
        }

        // List values: collect any child 'value' where idShort looks like list item
        var listValues = new List<string>();
        foreach (var child in children)
        {
            var idShort = GetString(child, "idShort") ?? GetString(child, "IdShort") ?? string.Empty;
            if (string.Equals(idShort, "embedding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var childValue = GetString(child, "value") ?? GetString(child, "Value");
            if (string.IsNullOrWhiteSpace(childValue))
            {
                continue;
            }

            if (LooksLikeEmbedding(childValue!))
            {
                continue;
            }

            listValues.Add(childValue!);
        }

        listValues = listValues
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (listValues.Count > 1)
        {
            return (null, null, null, listValues);
        }

        // Single value
        var value = listValues.Count == 1
            ? listValues[0]
            : FindValueInChildren(children, "value");

        return (value, null, null, Array.Empty<string>());
    }

    private static string NormalizeContainerName(string? idShort)
    {
        if (string.IsNullOrWhiteSpace(idShort))
        {
            return string.Empty;
        }

        var trimmed = idShort.Trim();

        trimmed = TrimSuffix(trimmed, "Container");
        trimmed = TrimSuffix(trimmed, "Range");
        trimmed = TrimSuffix(trimmed, "List");
        trimmed = TrimSuffix(trimmed, "Fixed");
        return trimmed;
    }

    private static string TrimSuffix(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - suffix.Length)
            : value;
    }

    private static bool LooksLikeEmbedding(string value)
    {
        if (value.Length > 256 && value.Contains(','))
        {
            return true;
        }

        // Heuristic: very many comma-separated numeric tokens.
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 32)
        {
            var numeric = 0;
            foreach (var p in parts.Take(40))
            {
                if (double.TryParse(p.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    numeric++;
                }
            }

            if (numeric >= 24)
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindValueInChildren(IReadOnlyCollection<INode> children, string key)
    {
        foreach (var child in children)
        {
            var idShort = GetString(child, "idShort") ?? GetString(child, "IdShort") ?? string.Empty;
            if (string.Equals(idShort, key, StringComparison.OrdinalIgnoreCase))
            {
                var value = GetString(child, "value") ?? GetString(child, "Value");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            // direct property with the requested key (e.g. child.min = '2.5' or child.Min)
            var direct = GetString(child, key) ?? GetString(child, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key));
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            // Some graphs represent ranges as child nodes like (:RangeValue { type: 'min', value: '2.5' })
            var childType = GetString(child, "type") ?? GetString(child, "Type");
            if (!string.IsNullOrWhiteSpace(childType) && string.Equals(childType, key, StringComparison.OrdinalIgnoreCase))
            {
                var typedValue = GetString(child, "value") ?? GetString(child, "Value");
                if (!string.IsNullOrWhiteSpace(typedValue))
                {
                    return typedValue;
                }
            }
        }

        return null;
    }

    private static string? GetString(INode node, string key)
    {
        if (node.Properties.TryGetValue(key, out var value) && value != null)
        {
            return value.ToString();
        }

        return null;
    }

    private static IReadOnlyList<string> TryParseStringList(object? value)
    {
        if (value == null)
        {
            return Array.Empty<string>();
        }

        if (value is IList<object> list)
        {
            return list.Select(v => v?.ToString() ?? string.Empty)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
        }

        // Some exports store list values as comma-separated string.
        if (value is string s)
        {
            return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
        }

        return Array.Empty<string>();
    }

    internal static double[]? TryParseEmbedding(string? embedding)
    {
        if (string.IsNullOrWhiteSpace(embedding))
        {
            return null;
        }

        var parts = embedding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return null;
            }
            values[i] = parsed;
        }

        return values;
    }

    private static double? TryParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

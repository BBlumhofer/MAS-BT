using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MAS_BT.Tools;

/// <summary>
/// Central JSON gateway for MAS-BT.
/// Goal: keep System.Text.Json usage isolated to this file.
/// Consumers should work with plain object graphs (Dictionary/List/primitives).
/// </summary>
public static class JsonFacade
{
    public static object? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return ToObject(node);
    }

    public static object? ParseFile(string path)
    {
        var full = Path.GetFullPath(path);
        var json = File.ReadAllText(full);
        return Parse(json);
    }

    public static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public static string Serialize(object? value, bool indented = false)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = indented });
    }

    public static object? GetPath(object? root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current is IDictionary<string, object?> dict)
            {
                if (!dict.TryGetValue(segment, out current))
                {
                    return null;
                }

                continue;
            }

            if (current is IReadOnlyDictionary<string, object?> roDict)
            {
                if (!roDict.TryGetValue(segment, out current))
                {
                    return null;
                }

                continue;
            }

            if (current is IList<object?> list)
            {
                if (!int.TryParse(segment, out var idx) || idx < 0 || idx >= list.Count)
                {
                    return null;
                }

                current = list[idx];
                continue;
            }

            return null;
        }

        return current;
    }

    public static string? GetPathAsString(object? root, IEnumerable<string> path)
    {
        var value = GetPath(root, path);
        return ToStringValue(value);
    }

    public static bool TryGetPathAsBool(object? root, IEnumerable<string> path, out bool value)
    {
        var v = GetPath(root, path);
        return TryToBool(v, out value);
    }

    public static bool TryToBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                result = parsed;
                return true;
            case string s when int.TryParse(s, out var n):
                result = n != 0;
                return true;
            case int i:
                result = i != 0;
                return true;
            case long l:
                result = l != 0;
                return true;
            case double d:
                result = Math.Abs(d) > double.Epsilon;
                return true;
            default:
                result = false;
                return false;
        }
    }

    public static bool TryToInt(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                result = (int)l;
                return true;
            case double d when d >= int.MinValue && d <= int.MaxValue:
                result = (int)d;
                return true;
            case string s when int.TryParse(s, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    public static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    public static string? ToStringValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            bool b => b ? bool.TrueString : bool.FalseString,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static object? ToObject(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kvp in obj)
            {
                dict[kvp.Key] = ToObject(kvp.Value);
            }
            return dict;
        }

        if (node is JsonArray arr)
        {
            var list = new List<object?>(arr.Count);
            foreach (var element in arr)
            {
                list.Add(ToObject(element));
            }
            return list;
        }

        if (node is JsonValue val)
        {
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<int>(out var i)) return i;
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<string>(out var s)) return s;
            return val.ToString();
        }

        return node.ToJsonString();
    }

    public static IEnumerable<string> EnumerateObjectKeys(object? node)
    {
        if (node is IDictionary<string, object?> dict)
        {
            return dict.Keys;
        }

        if (node is IReadOnlyDictionary<string, object?> roDict)
        {
            return roDict.Keys;
        }

        return Enumerable.Empty<string>();
    }
}

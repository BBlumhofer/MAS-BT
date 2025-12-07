using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using UAClient.Client;

namespace MAS_BT.Nodes.SkillControl;

internal static class SkillFinalResultHelper
{
    public static object? NormalizeValue(object? raw)
    {
        if (raw == null) return null;

        switch (raw)
        {
            case string s:
                return s;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return raw;
            case NodeId nodeId:
                return nodeId.ToString();
            case ExpandedNodeId expandedNodeId:
                return expandedNodeId.ToString();
            case QualifiedName qualifiedName:
                return qualifiedName.ToString();
            case LocalizedText localizedText:
                return localizedText.Text;
        }

        var type = raw.GetType();
        var prop = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null)
        {
            var inner = prop.GetValue(raw);
            if (!ReferenceEquals(inner, raw))
            {
                return NormalizeValue(inner);
            }
        }

        var field = type.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            var inner = field.GetValue(raw);
            if (!ReferenceEquals(inner, raw))
            {
                return NormalizeValue(inner);
            }
        }

        try
        {
            return JsonSerializer.Serialize(raw);
        }
        catch
        {
            return raw.ToString();
        }
    }

    public static Dictionary<string, object?> NormalizeSnapshot(IDictionary<string, object?> snapshot)
    {
        var normalized = new Dictionary<string, object?>(snapshot.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in snapshot)
        {
            normalized[kv.Key] = NormalizeValue(kv.Value);
        }
        return normalized;
    }

    public static bool HasMeaningfulData(IDictionary<string, object?>? data)
    {
        if (data == null || data.Count == 0) return false;
        foreach (var kv in data)
        {
            var value = NormalizeValue(kv.Value);
            if (value == null) continue;
            if (value is string s)
            {
                if (!string.IsNullOrWhiteSpace(s)) return true;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    public static bool TryConvertToLong(object? value, out long result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case uint ui:
                result = Convert.ToInt64(ui);
                return true;
            case ushort us:
                result = us;
                return true;
            case ulong ul when ul <= long.MaxValue:
                result = Convert.ToInt64(ul);
                return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                result = Convert.ToInt64(f);
                return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                result = Convert.ToInt64(d);
                return true;
            case decimal dec when dec <= long.MaxValue && dec >= long.MinValue:
                result = Convert.ToInt64(dec);
                return true;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
        }

        try
        {
            if (value is IConvertible convertible)
            {
                result = convertible.ToInt64(CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            // ignored, fall through to return false
        }

        result = 0;
        return false;
    }

    public static long? TryGetSuccessfulExecutionsCount(IDictionary<string, object?>? data)
    {
        if (data == null || data.Count == 0) return null;
        foreach (var kv in data)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (kv.Key.IndexOf("SuccessfulExecutionsCount", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (TryConvertToLong(kv.Value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static string ToDisplayString(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            _ => normalized?.ToString() ?? string.Empty
        };
    }

    public static async Task<IDictionary<string, object?>?> TryFetchSnapshotAsync(RemoteServer server, string moduleName, string skillName, ILogger logger)
    {
        if (server == null) return null;
        if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(skillName)) return null;

        if (!server.Modules.TryGetValue(moduleName, out var module))
        {
            logger.LogWarning("SkillFinalResultHelper: Module '{Module}' not found while retrieving FinalResultData for skill '{Skill}'", moduleName, skillName);
            return null;
        }

        var skill = ResolveSkill(module, skillName);
        if (skill == null)
        {
            logger.LogWarning("SkillFinalResultHelper: Skill '{Skill}' not found on module '{Module}'", skillName, moduleName);
            return null;
        }

        try
        {
            var snapshot = await skill.ReadFinalResultDataSnapshotAsync();
            if (snapshot == null || snapshot.Count == 0) return null;
            return NormalizeSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SkillFinalResultHelper: Failed to read FinalResultData for skill '{Skill}'", skillName);
            return null;
        }
    }

    public static RemoteSkill? ResolveSkill(RemoteModule module, string skillName)
    {
        if (module.SkillSet.TryGetValue(skillName, out var direct)) return direct;
        var normalized = skillName?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;

        var match = module.SkillSet.Values.FirstOrDefault(s => string.Equals(s.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        match = module.SkillSet.Values.FirstOrDefault(s => !string.IsNullOrEmpty(s.Name) && s.Name.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
        if (match != null) return match;

        var alt = normalized.EndsWith("Skill", StringComparison.OrdinalIgnoreCase) ? normalized[..^5] : normalized + "Skill";
        match = module.SkillSet.Values.FirstOrDefault(s => string.Equals(s.Name, alt, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        if (normalized.EndsWith("Skill", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = normalized[..^5];
            match = module.SkillSet.Values.FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return null;
    }
}

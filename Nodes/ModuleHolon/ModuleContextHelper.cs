using System;
using System.Collections.Generic;
using System.Text.Json;
using MAS_BT.Core;

namespace MAS_BT.Nodes.ModuleHolon;

public static class ModuleContextHelper
{
    private static string? GetString(BTContext context, string key)
    {
        var s = context.Get<string>(key);
        if (!string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        // Many config.* keys are stored as JsonElement in the blackboard.
        var json = context.Get<JsonElement>(key);
        if (json.ValueKind == JsonValueKind.String)
        {
            var v = json.GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        if (json.ValueKind == JsonValueKind.Number
            || json.ValueKind == JsonValueKind.True
            || json.ValueKind == JsonValueKind.False)
        {
            var v = json.ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        return null;
    }

    public static string ResolveModuleId(BTContext context)
    {
        // Prefer stable module identifiers for topics/cache keys.
        // Agent ids (e.g. P102_Execution) must not override module id (e.g. P102).
        return GetString(context, "ModuleId")
               ?? GetString(context, "config.Agent.ModuleId")
               ?? GetString(context, "config.Agent.ModuleName")
               ?? GetString(context, "AgentId")
               ?? GetString(context, "config.Agent.AgentId")
               ?? context.AgentId
               ?? "Module";
    }

    public static IReadOnlyCollection<string> ResolveModuleIdentifiers(BTContext context)
    {
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                identifiers.Add(value);
            }
        }

        TryAdd(GetString(context, "AgentId"));
        TryAdd(GetString(context, "config.Agent.AgentId"));
        TryAdd(GetString(context, "ModuleId"));
        TryAdd(GetString(context, "config.Agent.ModuleId"));
        TryAdd(GetString(context, "config.Agent.ModuleName"));
        TryAdd(context.AgentId);

        if (identifiers.Count == 0)
        {
            identifiers.Add("Module");
        }

        return identifiers;
    }
}

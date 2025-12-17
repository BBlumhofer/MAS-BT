using System;
using System.Collections.Generic;
using MAS_BT.Core;
using MAS_BT.Tools;

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

        var raw = context.Get(key);
        var v2 = JsonFacade.ToStringValue(raw);
        return string.IsNullOrWhiteSpace(v2) ? null : v2;
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

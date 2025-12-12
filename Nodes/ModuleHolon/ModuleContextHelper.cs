using MAS_BT.Core;

namespace MAS_BT.Nodes.ModuleHolon;

public static class ModuleContextHelper
{
    public static string ResolveModuleId(BTContext context)
    {
        return context.Get<string>("AgentId")
               ?? context.Get<string>("config.Agent.AgentId")
               ?? context.Get<string>("ModuleId")
               ?? context.Get<string>("config.Agent.ModuleName")
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

        TryAdd(context.Get<string>("AgentId"));
        TryAdd(context.Get<string>("config.Agent.AgentId"));
        TryAdd(context.Get<string>("ModuleId"));
        TryAdd(context.Get<string>("config.Agent.ModuleId"));
        TryAdd(context.Get<string>("config.Agent.ModuleName"));
        TryAdd(context.AgentId);

        if (identifiers.Count == 0)
        {
            identifiers.Add("Module");
        }

        return identifiers;
    }
}

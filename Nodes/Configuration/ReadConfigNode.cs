using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using System.Globalization;
using System.Text.Json;
using System.IO;
using System.Linq;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// ReadConfig - Reads configuration from a JSON file
/// </summary>
public class ReadConfigNode : BTNode
{
    public string ConfigPath { get; set; } = "config.json";
    
    public ReadConfigNode() : base("ReadConfig")
    {
    }
    
    public override async Task<NodeStatus> Execute()
    {
        Logger.LogDebug("ReadConfig: Loading configuration from {ConfigPath}", ConfigPath);
        
        try
        {
            var resolvedPath = string.IsNullOrWhiteSpace(ConfigPath)
                ? "config.json"
                : ConfigPath;

            // store the resolved path for downstream consumers (e.g., sub-holon spawning)
            Context.Set("config.Path", Path.GetFullPath(resolvedPath));
            Context.Set("config.Directory", Path.GetDirectoryName(Path.GetFullPath(resolvedPath)) ?? string.Empty);

            if (!File.Exists(resolvedPath))
            {
                var fallback = FindConfigUnderConfigs(resolvedPath);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    Logger.LogInformation("ReadConfig: Falling back to {Fallback} for missing config {ConfigPath}", fallback, resolvedPath);
                    resolvedPath = fallback;
                    Context.Set("config.Path", Path.GetFullPath(resolvedPath));
                    Context.Set("config.Directory", Path.GetDirectoryName(Path.GetFullPath(resolvedPath)) ?? string.Empty);
                }
            }

            if (!File.Exists(resolvedPath))
            {
                Logger.LogWarning("ReadConfig: Configuration file not found: {ConfigPath}", resolvedPath);
                return NodeStatus.Failure;
            }
            var jsonContent = await File.ReadAllTextAsync(resolvedPath);
            var config = JsonSerializer.Deserialize<JsonElement>(jsonContent);
            
            Context.Set("config", config);
            if (config.ValueKind == JsonValueKind.Object)
            {
                FlattenObject(config, "config");

                // Preserve the reserved path key for downstream consumers (FlattenObject may overwrite it
                // if the JSON contains a top-level "Path" field).
                Context.Set("config.Path", Path.GetFullPath(resolvedPath));
                Context.Set("config.Directory", Path.GetDirectoryName(Path.GetFullPath(resolvedPath)) ?? string.Empty);
            }
            else
            {
                Logger.LogWarning("ReadConfig: Expected root object in configuration file {ConfigPath}", resolvedPath);
            }
            
            Logger.LogInformation("ReadConfig: Successfully loaded configuration from {ConfigPath}", resolvedPath);
            
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ReadConfig: Error loading configuration from {ConfigPath}", ConfigPath);
            return NodeStatus.Failure;
        }
    }

    private static string? FindConfigUnderConfigs(string requestedPath)
    {
        var fileName = Path.GetFileName(requestedPath);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var configsRoot = Path.Combine(Directory.GetCurrentDirectory(), "configs");
        if (!Directory.Exists(configsRoot))
            return null;

        return Directory.EnumerateFiles(configsRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private void FlattenObject(JsonElement element, string prefix)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            Context.Set(prefix, element);
            foreach (var property in element.EnumerateObject())
            {
                var childPrefix = string.IsNullOrEmpty(prefix)
                    ? property.Name
                    : $"{prefix}.{property.Name}";
                FlattenObject(property.Value, childPrefix);
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            Context.Set(prefix, element);
            return;
        }

        Context.Set(prefix, ConvertPrimitive(element));
    }

    private static object? ConvertPrimitive(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue.ToString(CultureInfo.InvariantCulture)
                : element.TryGetDouble(out var doubleValue)
                    ? doubleValue.ToString(CultureInfo.InvariantCulture)
                    : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}

using Microsoft.Extensions.Logging;
using MAS_BT.Core;
using System.IO;
using System.Linq;
using MAS_BT.Tools;

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
            var config = JsonFacade.Parse(jsonContent);
            
            Context.Set("config", config);
            if (config is System.Collections.Generic.IDictionary<string, object?>)
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

    private void FlattenObject(object? element, string prefix)
    {
        if (element is System.Collections.Generic.IDictionary<string, object?> obj)
        {
            Context.Set(prefix, element);
            foreach (var property in obj)
            {
                var childPrefix = string.IsNullOrEmpty(prefix)
                    ? property.Key
                    : $"{prefix}.{property.Key}";
                FlattenObject(property.Value, childPrefix);
            }
            return;
        }

        if (element is System.Collections.Generic.IList<object?>)
        {
            Context.Set(prefix, element);
            return;
        }

        Context.Set(prefix, element);
    }
}

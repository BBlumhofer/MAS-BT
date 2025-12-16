using System;
using System.Collections.Generic;
using System.Linq;
using MAS_BT.Core;

namespace MAS_BT.Nodes.Common;

/// <summary>
/// Helper for building MQTT topic paths following the hierarchical pattern:
/// /{namespace}/{parent_agent}/{subagent}/topic
/// Supports recursive namespaces: /Factory/_PHUKET/P102/P102_ExecutionHolon/SkillResponse
/// </summary>
public static class TopicHelper
{
    /// <summary>
    /// Builds a topic path with hierarchical namespace support.
    /// Pattern: /{namespace_path}/{parentAgent}/{subAgent}/topic
    /// 
    /// Examples:
    /// - Simple: /_PHUKET/P102/P102_ExecutionHolon/SkillResponse
    /// - Nested: /Factory/_PHUKET/P102/P102_ExecutionHolon/SkillResponse
    /// 
    /// Configuration:
    /// - Namespace: "_PHUKET" or "Factory/_PHUKET"
    /// - ParentNamespace (optional): adds parent hierarchy
    /// </summary>
    public static string BuildTopic(BTContext context, string topic)
    {
        var namespacePath = BuildNamespacePath(context);
        var parentAgent = context.Get<string>("config.Agent.ModuleId") 
                          ?? context.Get<string>("ModuleId") 
                          ?? context.Get<string>("config.Agent.AgentId") 
                          ?? context.AgentId;
        var role = context.Get<string>("config.Agent.Role") ?? context.Get<string>("Role") ?? "";
        
        // SubAgent = {ParentAgent}_{Role}
        var subAgent = !string.IsNullOrEmpty(role) ? $"{parentAgent}_{role}" : parentAgent;
        
        return $"{namespacePath}/{parentAgent}/{subAgent}/{topic}";
    }

    /// <summary>
    /// Builds a namespace-level topic: /{namespace_path}/topic
    /// 
    /// Examples:
    /// - Simple: /_PHUKET/register
    /// - Nested: /Factory/_PHUKET/register
    /// </summary>
    public static string BuildNamespaceTopic(BTContext context, string topic)
    {
        var namespacePath = BuildNamespacePath(context);
        return $"{namespacePath}/{topic}";
    }

    /// <summary>
    /// Builds the hierarchical namespace path from context configuration.
    /// Supports recursive parent namespaces.
    /// 
    /// Configuration options:
    /// 1. config.Namespace = "Factory/_PHUKET" → /Factory/_PHUKET
    /// 2. config.Namespace = "_PHUKET" + config.ParentNamespace = "Factory" → /Factory/_PHUKET
    /// </summary>
    private static string BuildNamespacePath(BTContext context)
    {
        var ns = context.Get<string>("config.Namespace") ?? context.Get<string>("Namespace") ?? "";
        var parentNs = context.Get<string>("config.ParentNamespace") ?? context.Get<string>("ParentNamespace");
        
        if (string.IsNullOrWhiteSpace(ns))
            return "";
        
        // Parse namespace - it might already contain hierarchy (e.g., "Factory/_PHUKET")
        var parts = ns.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        
        // Prepend parent namespace if specified separately
        if (!string.IsNullOrWhiteSpace(parentNs))
        {
            var parentParts = parentNs.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            parts.InsertRange(0, parentParts);
        }
        
        return "/" + string.Join("/", parts);
    }
}

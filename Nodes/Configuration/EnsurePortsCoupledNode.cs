using System;
using System.Linq;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using AasSharpClient.Models.Messages;
using UAClient.Client;

namespace MAS_BT.Nodes.Configuration;

/// <summary>
/// Ensures that every port with a CoupleSkill is running before startup is triggered.
/// </summary>
public class EnsurePortsCoupledNode : BTNode
{
    public string ModuleName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public EnsurePortsCoupledNode() : base("EnsurePortsCoupled")
    {
    }

    public override async Task<NodeStatus> Execute()
    {
        var resolvedModuleName = ResolveModuleName();
        if (string.IsNullOrWhiteSpace(resolvedModuleName))
        {
            Logger.LogError("EnsurePortsCoupled: ModuleName not provided");
            UpdateCouplingFlags(false);
            return NodeStatus.Failure;
        }

        var server = Context.Get<RemoteServer>("RemoteServer");
        if (server == null)
        {
            Logger.LogError("EnsurePortsCoupled: RemoteServer not found in context");
            UpdateCouplingFlags(false);
            return NodeStatus.Failure;
        }

        if (!server.Modules.TryGetValue(resolvedModuleName, out var module))
        {
            Logger.LogError("EnsurePortsCoupled: Module {ModuleName} not found on RemoteServer", resolvedModuleName);
            UpdateCouplingFlags(false);
            return NodeStatus.Failure;
        }

        var client = Context.Get<UaClient>("UaClient");
        if (client?.Session == null)
        {
            Logger.LogError("EnsurePortsCoupled: UaClient session missing");
            UpdateCouplingFlags(false);
            return NodeStatus.Failure;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));
        var coupleCandidates = module.Ports.Values.ToList();
        if (coupleCandidates.Count == 0)
        {
            Logger.LogInformation("EnsurePortsCoupled: Module {ModuleName} exposes no ports", resolvedModuleName);
            UpdateCouplingFlags(true);
            return NodeStatus.Success;
        }

        var inspected = 0;
        var ready = 0;

        foreach (var port in coupleCandidates)
        {
            if (!PortSupportsCoupling(port))
            {
                Logger.LogDebug("EnsurePortsCoupled: Port {Port} has no CoupleSkill, skipping", port.Name);
                continue;
            }

            inspected++;
            try
            {
                var isCoupled = await port.IsCoupledAsync(client);
                if (isCoupled)
                {
                    ready++;
                    Logger.LogInformation("EnsurePortsCoupled: Port {Port} already coupled", port.Name);
                    continue;
                }

                Logger.LogInformation("EnsurePortsCoupled: Starting CoupleSkill for port {Port}", port.Name);
                await port.CoupleAsync(client, timeout);
                var afterCouple = await port.IsCoupledAsync(client);
                if (afterCouple)
                {
                    ready++;
                    Logger.LogInformation("EnsurePortsCoupled: Port {Port} reached Running", port.Name);
                    await SendLogMessageAsync(LogMessage.LogLevel.Info, $"Port {port.Name} coupled successfully.");
                }
                else
                {
                    Logger.LogWarning("EnsurePortsCoupled: Port {Port} did not reach Running", port.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "EnsurePortsCoupled: Error while coupling port {Port}", port.Name);
            }
        }

        if (inspected == 0)
        {
            Logger.LogInformation("EnsurePortsCoupled: No ports with CoupleSkill found on module {ModuleName}", resolvedModuleName);
            UpdateCouplingFlags(true);
            return NodeStatus.Success;
        }

        if (ready == inspected)
        {
            Logger.LogInformation("EnsurePortsCoupled: All {Count} coupled ports running", ready);
            await SendLogMessageAsync(LogMessage.LogLevel.Info, $"All {ready} couple ports running.");
            UpdateCouplingFlags(true);
            return NodeStatus.Success;
        }

        Logger.LogError("EnsurePortsCoupled: {Ready} of {Inspected} couple ports reached Running", ready, inspected);
        UpdateCouplingFlags(false);
        return NodeStatus.Failure;
    }

    private string ResolveModuleName()
    {
        var resolved = ResolvePlaceholders(ModuleName);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        return Context.Get<string>("MachineName") ?? string.Empty;
    }

    private static bool PortSupportsCoupling(RemotePort port)
    {
        try
        {
            if (port.SkillSet.Keys.Any(k => k.IndexOf("couple", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            if (port.Methods.Values.OfType<RemoteSkill>().Any(IsCoupleSkill))
                return true;

            if (port.Module != null)
            {
                if (port.Module.Methods.Values.OfType<RemoteSkill>().Any(IsCoupleSkill))
                    return true;
                if (port.Module.SkillSet.Values.Any(IsCoupleSkill))
                    return true;
            }
        }
        catch
        {
            // ignored - treat as no couple support
        }

        return false;
    }

    private static bool IsCoupleSkill(RemoteSkill skill)
    {
        return skill.Name.IndexOf("couple", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void UpdateCouplingFlags(bool isCoupled)
    {
        Context.Set("coupled", isCoupled);
        Context.Set("portsCoupled", isCoupled);
        Set("portsCoupled", isCoupled);
    }

    private async Task SendLogMessageAsync(LogMessage.LogLevel level, string message)
    {
        try
        {
            var messagingClient = Context.Get<MessagingClient>("MessagingClient");
            if (messagingClient == null || !messagingClient.IsConnected)
            {
                return;
            }

            var agentId = Context.AgentId ?? "UnknownAgent";
            var agentRole = Context.AgentRole ?? "ResourceHolon";
            var logElement = new LogMessage(level, message, agentRole, agentId);
            var builder = new I40MessageBuilder()
                .From(agentId, agentRole)
                .To("broadcast", string.Empty)
                .WithType(I40MessageTypes.INFORM)
                .WithConversationId(Guid.NewGuid().ToString())
                .AddElement(logElement);

            await messagingClient.PublishAsync(builder.Build(), $"{agentId}/logs");
        }
        catch
        {
            // ignore logging failures
        }
    }
}

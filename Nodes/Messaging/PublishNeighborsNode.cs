using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using System.Text.Json;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.Messaging;

/// <summary>
/// Publishes the neighbor list of the module to the shared topic so the ModuleHolon can cache it.
/// </summary>
public class PublishNeighborsNode : BTNode
{
    public string ModuleId { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public PublishNeighborsNode() : base("PublishNeighbors") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("PublishNeighbors: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleName = !string.IsNullOrWhiteSpace(ModuleName) ? ModuleName : ModuleId;
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            moduleName = Context.Get<string>("config.Agent.ModuleName") ?? Context.Get<string>("ModuleId") ?? Context.AgentId;
        }

        var neighbors = Context.Get<List<string>>("Neighbors") ?? new List<string>();
        var topic = $"/{ns}/{moduleName}/Neighbors";

        try
        {
            var payloadJson = JsonSerializer.Serialize(neighbors);
            var msg = new I40MessageBuilder()
                .From($"{moduleName}_Execution_Agent", "ExecutionAgent")
                .To("Broadcast", "System")
                .WithType("neighborsUpdate")
                .AddElement(new Property<string>("Neighbors") { Value = new PropertyValue<string>(payloadJson) })
                .Build();

            await client.PublishAsync(msg, topic);
            Logger.LogInformation("PublishNeighbors: published {Count} neighbors to {Topic}", neighbors.Count, topic);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "PublishNeighbors: failed to publish neighbors");
            return NodeStatus.Failure;
        }
    }
}

using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;

namespace MAS_BT.Nodes.ModuleHolon;

/// <summary>
/// Sends a sub-holon registration message to the parent ModuleHolon.
/// </summary>
public class RegisterSubHolonNode : BTNode
{
    public RegisterSubHolonNode() : base("RegisterSubHolon") { }

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null || !client.IsConnected)
        {
            Logger.LogError("RegisterSubHolon: MessagingClient unavailable");
            return NodeStatus.Failure;
        }

            var moduleId = Context.Get<string>("config.Agent.AgentId")
                       ?? Context.Get<string>("config.Agent.ModuleId")
                       ?? Context.Get<string>("config.OPCUA.ModuleName")
                       ?? Context.Get<string>("ModuleId")
                       ?? Context.AgentId;
        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var topic = $"/{ns}/{moduleId}/register";

        var state = Context.Get<string>("AgentState") ?? "Running";

            var registrationId = BuildRegistrationAgentId();

        try
        {
            var builder = new I40MessageBuilder()
                .From(registrationId, string.IsNullOrWhiteSpace(Context.AgentRole) ? "SubHolon" : Context.AgentRole)
                .To($"{moduleId}_ModuleHolon", null)
                .WithType("subHolonRegister")
                .WithConversationId(Guid.NewGuid().ToString());

            builder.AddElement(new Property<string>("ModuleId") { Value = new PropertyValue<string>(moduleId) });
            builder.AddElement(new Property<string>("Namespace") { Value = new PropertyValue<string>(ns) });
            builder.AddElement(new Property<string>("AgentId") { Value = new PropertyValue<string>(registrationId) });
            builder.AddElement(new Property<string>("State") { Value = new PropertyValue<string>(state) });

            var msg = builder.Build();
            await client.PublishAsync(msg, topic);
            Logger.LogInformation("RegisterSubHolon: sent registration for {ModuleId} to {Topic}", moduleId, topic);
            return NodeStatus.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RegisterSubHolon: failed to send registration");
            return NodeStatus.Failure;
        }
    }

    private string BuildRegistrationAgentId()
    {
        var baseId = Context.Get<string>("config.Agent.AgentId") ?? Context.AgentId ?? "SubHolon";
        var role = string.IsNullOrWhiteSpace(Context.AgentRole) ? "SubHolon" : Context.AgentRole;
        var pid = Environment.ProcessId.ToString();

        // Avoid whitespace in role segment for topic friendliness
        role = role.Replace(" ", "_");

        return $"{baseId}_{role}_{pid}";
    }
}

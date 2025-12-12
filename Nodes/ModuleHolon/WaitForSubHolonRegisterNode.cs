using System;
using System.Collections.Concurrent;
using AasSharpClient.Messages;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Nodes.ModuleHolon;

public class WaitForSubHolonRegisterNode : BTNode
{
    public WaitForSubHolonRegisterNode() : base("WaitForSubHolonRegister") {}

    public override async Task<NodeStatus> Execute()
    {
        var client = Context.Get<MessagingClient>("MessagingClient");
        if (client == null) return NodeStatus.Failure;

        var ns = Context.Get<string>("config.Namespace") ?? Context.Get<string>("Namespace") ?? "phuket";
        var moduleId = ModuleContextHelper.ResolveModuleId(Context);
        var topic = $"/{ns}/{moduleId}/register";

        var queue = new ConcurrentQueue<I40Message>();
        await client.SubscribeAsync(topic);
        client.OnMessage(m =>
        {
            if (m?.Frame?.Type != null && string.Equals(m.Frame.Type, "subHolonRegister", StringComparison.OrdinalIgnoreCase))
            {
                queue.Enqueue(m);
            }
        });

        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow - start < timeout)
        {
            if (queue.TryDequeue(out var msg))
            {
                var state = Context.Get<DispatchingState>("DispatchingState") ?? new DispatchingState();
                var info = DispatchingModuleInfo.FromMessage(msg);
                state.Upsert(info);
                Context.Set("DispatchingState", state);
                Logger.LogInformation("WaitForSubHolonRegister: registered sub-holon {Id}", info.ModuleId);
                return NodeStatus.Success;
            }
            await Task.Delay(100);
        }

        Logger.LogWarning("WaitForSubHolonRegister: timeout waiting for sub-holon registration on {Topic}", topic);
        return NodeStatus.Failure;
    }
}

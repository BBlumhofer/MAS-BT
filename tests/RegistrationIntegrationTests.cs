using System;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using MAS_BT.Nodes.Dispatching;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

public class RegistrationIntegrationTests
{
    [Fact]
    public async Task RegisterAgent_PlanningAgent_PublishesSubHolonRegisterToModuleTopic()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P102";
        var agentId = "P102_Planning";

        var transport = new InMemoryTransport();
        var sender = new MessagingClient(transport, $"{agentId}/logs");
        var receiver = new MessagingClient(transport, $"{moduleId}/logs");
        await sender.ConnectAsync();
        await receiver.ConnectAsync();

        var topic = $"/{ns}/{moduleId}/register";
        await receiver.SubscribeAsync(topic);

        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnMessage(m =>
        {
            if (!tcs.Task.IsCompleted &&
                string.Equals(m.Frame?.Type, "subHolonRegister", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(m);
            }
        });

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = agentId,
            AgentRole = "PlanningAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("config.Agent.AgentId", agentId);
        context.Set("config.Agent.ModuleId", moduleId);
        context.Set("MessagingClient", sender);

        var node = new RegisterAgentNode { Context = context };
        node.SetLogger(NullLogger<RegisterAgentNode>.Instance);

        var status = await node.Execute();
        Assert.Equal(NodeStatus.Success, status);

        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(received == tcs.Task, "Expected registration message not received");

        var message = await tcs.Task;
        Assert.NotNull(message);
        Assert.Equal("subHolonRegister", message!.Frame?.Type);
        var elements = message.InteractionElements ?? new System.Collections.Generic.List<ISubmodelElement>();
        Assert.Contains(elements,
            e => e is SubmodelElementCollection c && string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DispatchingModuleInfo_FromMessage_ExtractsAgentIdAndCapabilitiesFromRegisterMessage()
    {
        var reg = new RegisterMessage(
            agentId: "P102",
            subagents: new(),
            capabilities: new() { "CapA", "CapB" });

        var msg = new I40MessageBuilder()
            .From("P102", "ModuleHolon")
            .To("DispatchingAgent", "DispatchingAgent")
            .WithType("moduleRegistration")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(reg.ToSubmodelElementCollection())
            .Build();

        var info = DispatchingModuleInfo.FromMessage(msg);
        Assert.Equal("P102", info.ModuleId);
        Assert.Contains("CapA", info.Capabilities);
        Assert.Contains("CapB", info.Capabilities);
    }

    [Fact]
    public void DispatchingState_UpsertFromRegistration_DoesNotClobberExistingInventory()
    {
        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo
        {
            ModuleId = "P102",
            Capabilities = new() { "OldCap" },
            InventoryFree = 3,
            InventoryOccupied = 7,
            LastSeenUtc = DateTime.UtcNow
        });

        var reg = new RegisterMessage(
            agentId: "P102",
            subagents: new(),
            capabilities: new() { "NewCap" });

        var msg = new I40MessageBuilder()
            .From("P102", "ModuleHolon")
            .To("DispatchingAgent", "DispatchingAgent")
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(reg.ToSubmodelElementCollection())
            .Build();

        var info = DispatchingModuleInfo.FromMessage(msg);
        info.LastRegistrationUtc = DateTime.UtcNow;
        info.LastSeenUtc = info.LastRegistrationUtc;
        state.Upsert(info);

        var p102 = Assert.Single(state.Modules, m => string.Equals(m.ModuleId, "P102", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, p102.InventoryFree);
        Assert.Equal(7, p102.InventoryOccupied);
        Assert.Contains("NewCap", p102.Capabilities);
    }

    [Fact]
    public async Task WaitForRegistration_AllowsSameAgentIdWhenRolesDiffer()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P103";

        var transport = new InMemoryTransport();
        var holon = new MessagingClient(transport, "holon/logs");
        var pub = new MessagingClient(transport, "pub/logs");
        await holon.ConnectAsync();
        await pub.ConnectAsync();

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = moduleId,
            AgentRole = "ModuleHolon"
        };
        context.Set("config.Namespace", ns);
        context.Set("config.Agent.ModuleId", moduleId);
        context.Set("MessagingClient", holon);

        var wait = new WaitForRegistrationNode
        {
            Context = context,
            TimeoutSeconds = 2,
            ExpectedCount = 2,
            ExpectedTypes = "subHolonRegister"
        };
        wait.SetLogger(NullLogger<WaitForRegistrationNode>.Instance);

        var waitTask = wait.Execute();

        // Two sub-holons publish with the SAME AgentId (P103) but different roles.
        var planningMsg = new I40MessageBuilder()
            .From(moduleId, "PlanningHolon")
            .To(moduleId, "ModuleHolon")
            .WithType("subHolonRegister")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(new RegisterMessage(moduleId, new(), new()).ToSubmodelElementCollection())
            .Build();

        var execMsg = new I40MessageBuilder()
            .From(moduleId, "ExecutionHolon")
            .To(moduleId, "ModuleHolon")
            .WithType("subHolonRegister")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(new RegisterMessage(moduleId, new(), new()).ToSubmodelElementCollection())
            .Build();

        await pub.PublishAsync(planningMsg, $"/{ns}/{moduleId}/register");
        await pub.PublishAsync(execMsg, $"/{ns}/{moduleId}/register");

        var status = await waitTask;
        Assert.Equal(NodeStatus.Success, status);
    }

    [Fact]
    public async Task RegisterAgent_DispatchingAgent_PublishesRegisterToNamespaceWithSubagentsFromState()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = $"DispatchingAgent_{ns}";

        var transport = new InMemoryTransport();
        var sender = new MessagingClient(transport, $"{dispatchingId}/logs");
        var receiver = new MessagingClient(transport, "namespace/logs");
        await sender.ConnectAsync();
        await receiver.ConnectAsync();

        var topic = $"/{ns}/register";
        await receiver.SubscribeAsync(topic);

        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnMessage(m =>
        {
            if (!tcs.Task.IsCompleted &&
                string.Equals(m.Frame?.Type, "registerMessage", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(m);
            }
        });

        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo { ModuleId = "P102" });

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = dispatchingId,
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("config.Agent.AgentId", dispatchingId);
        context.Set("MessagingClient", sender);
        context.Set("DispatchingState", state);

        var node = new RegisterAgentNode { Context = context };
        node.SetLogger(NullLogger<RegisterAgentNode>.Instance);

        var status = await node.Execute();
        Assert.Equal(NodeStatus.Success, status);

        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(received == tcs.Task, "Expected namespace registration message not received");

        var msg = await tcs.Task;
        Assert.NotNull(msg);

        var regCollection = Assert.Single(msg!.InteractionElements,
            e => e is SubmodelElementCollection c && string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase)) as SubmodelElementCollection;
        Assert.NotNull(regCollection);

        var reg = RegisterMessage.FromSubmodelElementCollection(regCollection!);
        Assert.Contains("P102", reg.Subagents);
    }

    [Fact]
    public async Task RegisterAgent_DispatchingAgent_AggregatesCapabilitiesAndInventoryAcrossModules()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = $"DispatchingAgent_{ns}";

        var transport = new InMemoryTransport();
        var sender = new MessagingClient(transport, $"{dispatchingId}/logs");
        var receiver = new MessagingClient(transport, "namespace/logs");
        await sender.ConnectAsync();
        await receiver.ConnectAsync();

        var topic = $"/{ns}/register";
        await receiver.SubscribeAsync(topic);

        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnMessage(m =>
        {
            if (!tcs.Task.IsCompleted &&
                string.Equals(m.Frame?.Type, "registerMessage", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(m);
            }
        });

        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo
        {
            ModuleId = "P101",
            Capabilities = new() { "Assemble", "Weld" },
            InventoryFree = 2,
            InventoryOccupied = 1
        });
        state.Upsert(new DispatchingModuleInfo
        {
            ModuleId = "P102",
            Capabilities = new() { "Assemble" },
            InventoryFree = 3,
            InventoryOccupied = 4
        });

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = dispatchingId,
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("config.Agent.AgentId", dispatchingId);
        context.Set("MessagingClient", sender);
        context.Set("DispatchingState", state);

        var node = new RegisterAgentNode { Context = context };
        node.SetLogger(NullLogger<RegisterAgentNode>.Instance);

        var status = await node.Execute();
        Assert.Equal(NodeStatus.Success, status);

        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(received == tcs.Task, "Expected namespace registration message not received");

        var msg = await tcs.Task;
        Assert.NotNull(msg);

        var regCollection = msg!.InteractionElements.OfType<SubmodelElementCollection>()
            .FirstOrDefault(c => string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(regCollection);

        var reg = RegisterMessage.FromSubmodelElementCollection(regCollection!);

        // Capabilities: duplicates allowed -> Assemble appears twice (P101 + P102)
        Assert.Equal(2, reg.Capabilities.Count(c => string.Equals(c, "Assemble", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Weld", reg.Capabilities);

        var summary = msg.InteractionElements.OfType<SubmodelElementCollection>()
            .FirstOrDefault(c => string.Equals(c.IdShort, "InventorySummary", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(summary);

        var freeProp = summary!.Value.Value.OfType<Property>().First(p => string.Equals(p.IdShort, "free", StringComparison.OrdinalIgnoreCase));
        var occupiedProp = summary.Value.Value.OfType<Property>().First(p => string.Equals(p.IdShort, "occupied", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(5, freeProp.Value.Value.ToObject<int>());
        Assert.Equal(5, occupiedProp.Value.Value.ToObject<int>());
    }

    [Fact]
    public async Task RegisterAgent_DispatchingAgent_PrunesStaleModulesByTimeout()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = $"DispatchingAgent_{ns}";

        var transport = new InMemoryTransport();
        var sender = new MessagingClient(transport, $"{dispatchingId}/logs");
        var receiver = new MessagingClient(transport, "namespace/logs");
        await sender.ConnectAsync();
        await receiver.ConnectAsync();

        var topic = $"/{ns}/register";
        await receiver.SubscribeAsync(topic);

        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnMessage(m =>
        {
            if (!tcs.Task.IsCompleted &&
                string.Equals(m.Frame?.Type, "registerMessage", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(m);
            }
        });

        var now = DateTime.UtcNow;
        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo
        {
            ModuleId = "P101",
            Capabilities = new() { "Assemble" },
            InventoryFree = 1,
            InventoryOccupied = 2,
            LastSeenUtc = now - TimeSpan.FromSeconds(5)
        });
        state.Upsert(new DispatchingModuleInfo
        {
            ModuleId = "P102",
            Capabilities = new() { "Weld" },
            InventoryFree = 10,
            InventoryOccupied = 20,
            LastSeenUtc = now - TimeSpan.FromSeconds(45)
        });

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = dispatchingId,
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("config.Agent.AgentId", dispatchingId);
        context.Set("config.DispatchingAgent.AgentTimeoutSeconds", 30);
        context.Set("MessagingClient", sender);
        context.Set("DispatchingState", state);

        var node = new RegisterAgentNode { Context = context };
        node.SetLogger(NullLogger<RegisterAgentNode>.Instance);

        var status = await node.Execute();
        Assert.Equal(NodeStatus.Success, status);

        var received = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(received == tcs.Task, "Expected namespace registration message not received");

        var msg = await tcs.Task;
        Assert.NotNull(msg);

        var regCollection = msg!.InteractionElements.OfType<SubmodelElementCollection>()
            .FirstOrDefault(c => string.Equals(c.IdShort, "RegisterMessage", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(regCollection);

        var reg = RegisterMessage.FromSubmodelElementCollection(regCollection!);
        Assert.Contains("P101", reg.Subagents);
        Assert.DoesNotContain("P102", reg.Subagents);
        Assert.Contains("Assemble", reg.Capabilities);
        Assert.DoesNotContain("Weld", reg.Capabilities);

        var summary = msg.InteractionElements.OfType<SubmodelElementCollection>()
            .FirstOrDefault(c => string.Equals(c.IdShort, "InventorySummary", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(summary);

        var freeProp = summary!.Value.Value.OfType<Property>().First(p => string.Equals(p.IdShort, "free", StringComparison.OrdinalIgnoreCase));
        var occupiedProp = summary.Value.Value.OfType<Property>().First(p => string.Equals(p.IdShort, "occupied", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, freeProp.Value.Value.ToObject<int>());
        Assert.Equal(2, occupiedProp.Value.Value.ToObject<int>());
    }

    [Fact]
    public async Task SubscribeAgentTopics_DispatchingAgent_UpdatesStateFromIncomingRegistration()
    {
        var ns = $"test{Guid.NewGuid():N}";

        var transport = new InMemoryTransport();
        var dispatching = new MessagingClient(transport, "dispatch/default");
        var publisher = new MessagingClient(transport, "pub/default");
        await dispatching.ConnectAsync();
        await publisher.ConnectAsync();

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = $"DispatchingAgent_{ns}",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("MessagingClient", dispatching);
        context.Set("DispatchingState", new DispatchingState());

        var subNode = new SubscribeAgentTopicsNode { Context = context, Role = "DispatchingAgent" };
        subNode.SetLogger(NullLogger<SubscribeAgentTopicsNode>.Instance);
        Assert.Equal(NodeStatus.Success, await subNode.Execute());

        var reg = new RegisterMessage("P102", new() { "P102_Planning", "P102_Execution" }, new() { "Assemble" });
        var msg = new I40MessageBuilder()
            .From("P102", "ModuleHolon")
            .To("DispatchingAgent", "DispatchingAgent")
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(reg.ToSubmodelElementCollection())
            .Build();

        await publisher.PublishAsync(msg, $"/{ns}/DispatchingAgent/register");

        // callbacks are synchronous in the in-memory transport, but give it a short breath
        await Task.Delay(50);

        var state = context.Get<DispatchingState>("DispatchingState");
        Assert.NotNull(state);
        Assert.Contains(state!.Modules, m => string.Equals(m.ModuleId, "P102", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.Modules.First(m => m.ModuleId == "P102").Capabilities, c => c == "Assemble");
    }

    [Fact]
    public async Task SubscribeAgentTopics_DispatchingAgent_UpdatesInventoryFromIncomingInventoryUpdate()
    {
        var ns = $"test{Guid.NewGuid():N}";

        var transport = new InMemoryTransport();
        var dispatching = new MessagingClient(transport, "dispatch/default");
        var publisher = new MessagingClient(transport, "pub/default");
        await dispatching.ConnectAsync();
        await publisher.ConnectAsync();

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = $"DispatchingAgent_{ns}",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("MessagingClient", dispatching);
        context.Set("DispatchingState", new DispatchingState());

        var subNode = new SubscribeAgentTopicsNode { Context = context, Role = "DispatchingAgent" };
        subNode.SetLogger(NullLogger<SubscribeAgentTopicsNode>.Instance);
        Assert.Equal(NodeStatus.Success, await subNode.Execute());

        // Build StorageUnits with embedded InventorySummary
        var storageUnits = new SubmodelElementCollection("StorageUnits");
        var summary = new SubmodelElementCollection("InventorySummary");
        var freeProp = new Property("free", new DataType(DataObjectType.Integer)) { Value = new PropertyValue<int>(3) };
        var occProp = new Property("occupied", new DataType(DataObjectType.Integer)) { Value = new PropertyValue<int>(7) };
        summary.Add(freeProp);
        summary.Add(occProp);
        storageUnits.Add(summary);

        var invMsg = new I40MessageBuilder()
            .From("P102_Execution", "ExecutionAgent")
            .To("Broadcast", "System")
            .WithType("inventoryUpdate")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(storageUnits)
            .Build();

        await publisher.PublishAsync(invMsg, $"/{ns}/P102/Inventory");
        await Task.Delay(50);

        var state = context.Get<DispatchingState>("DispatchingState");
        Assert.NotNull(state);
        var p102 = state!.Modules.FirstOrDefault(m => string.Equals(m.ModuleId, "P102", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(p102);
        Assert.Equal(3, p102!.InventoryFree);
        Assert.Equal(7, p102.InventoryOccupied);
    }

    [Fact]
    public async Task SubscribeAgentTopics_DispatchingAgent_ReceivesOfferResponsesOnOfferTopic()
    {
        var ns = $"test{Guid.NewGuid():N}";

        var transport = new InMemoryTransport();
        var dispatching = new MessagingClient(transport, "dispatch/default");
        var publisher = new MessagingClient(transport, "pub/default");
        await dispatching.ConnectAsync();
        await publisher.ConnectAsync();

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = $"DispatchingAgent_{ns}",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("MessagingClient", dispatching);
        context.Set("DispatchingState", new DispatchingState());

        var subNode = new SubscribeAgentTopicsNode { Context = context, Role = "DispatchingAgent" };
        subNode.SetLogger(NullLogger<SubscribeAgentTopicsNode>.Instance);
        Assert.Equal(NodeStatus.Success, await subNode.Execute());

        var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatching.OnMessage(m => tcs.TrySetResult(m));

        var conv = Guid.NewGuid().ToString();
        var msg = new I40MessageBuilder()
            .From("P102_Planning", "PlanningHolon")
            .To("DispatchingAgent", "DispatchingAgent")
            .WithType(I40MessageTypes.PROPOSAL)
            .WithConversationId(conv)
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("Assemble") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-1") })
            .Build();

        await publisher.PublishAsync(msg, $"/{ns}/DispatchingAgent/Offer");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(tcs.Task, completed);
        Assert.NotNull(tcs.Task.Result);
        Assert.Equal(I40MessageTypes.PROPOSAL, tcs.Task.Result!.Frame?.Type);
        Assert.Equal(conv, tcs.Task.Result.Frame?.ConversationId);
    }
}

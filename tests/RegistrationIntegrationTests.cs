using System;
using System.Threading.Tasks;
using System.Threading;
using AAS_Sharp_Client.Models.Messages;
using AasSharpClient.Models.Helpers;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using MAS_BT.Nodes.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

public class RegistrationIntegrationTests
{
    [Fact]
    public async Task RegisterAgent_PlanningAgent_PublishesRegisterMessageToModuleTopic()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P102";
        var agentId = "P102_Planning";

        await using var senderHandle = await CreateClientAsync($"{agentId}_sender");
        await using var receiverHandle = await CreateClientAsync($"{moduleId}_receiver");
        var sender = senderHandle.Client;
        var receiver = receiverHandle.Client;

        var topic = $"/{ns}/{moduleId}/register";
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
        Assert.Equal("registerMessage", message!.Frame?.Type);
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

        await using var holonHandle = await CreateClientAsync($"{moduleId}_holon");
        await using var pubHandle = await CreateClientAsync($"{moduleId}_publisher");
        var holon = holonHandle.Client;
        var pub = pubHandle.Client;

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
            ExpectedTypes = "registerMessage"
        };
        wait.SetLogger(NullLogger<WaitForRegistrationNode>.Instance);

        var waitTask = wait.Execute();
        await Task.Delay(200);

        // Two sub-holons publish with the SAME AgentId (P103) but different roles.
        var planningMsg = new I40MessageBuilder()
            .From(moduleId, "PlanningHolon")
            .To(moduleId, "ModuleHolon")
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(new RegisterMessage(moduleId, new(), new()).ToSubmodelElementCollection())
            .Build();

        var execMsg = new I40MessageBuilder()
            .From(moduleId, "ExecutionHolon")
            .To(moduleId, "ModuleHolon")
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(new RegisterMessage(moduleId, new(), new()).ToSubmodelElementCollection())
            .Build();

        await pub.PublishAsync(planningMsg, $"/{ns}/{moduleId}/register");
        await pub.PublishAsync(execMsg, $"/{ns}/{moduleId}/register");

        var status = await waitTask;
        Assert.Equal(NodeStatus.Success, status);
    }

    [Fact]
    public async Task WaitForRegistration_NamespaceHolon_CompletesWhenExpectedAgentsRegister()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = "ManufacturingDispatcher_phuket";
        var transportId = "TransportManager_phuket";

        await using var namespaceHandle = await CreateClientAsync($"{ns}_namespace");
        var namespaceClient = namespaceHandle.Client;

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = ns,
            AgentRole = "NamespaceHolon"
        };
        context.Set("config.Namespace", ns);
        context.Set("MessagingClient", namespaceClient);

        var wait = new WaitForRegistrationNode
        {
            Context = context,
            TimeoutSeconds = 3,
            ExpectedCount = 2,
            ExpectedAgents = $"{dispatchingId},{transportId}",
            ExpectedTypes = "registerMessage",
            TopicOverride = $"/{ns}/register"
        };
        wait.SetLogger(NullLogger<WaitForRegistrationNode>.Instance);

        var waitTask = wait.Execute();
        await Task.Delay(250);
        var rootTopic = $"/{ns}/register";
        await using var dispatchPublisher = await CreateClientAsync($"{dispatchingId}_pub");
        await using var transportPublisher = await CreateClientAsync($"{transportId}_pub");
        await PublishRegisterAsync(dispatchPublisher.Client, ns, dispatchingId, "Dispatching", rootTopic);
        await PublishRegisterAsync(transportPublisher.Client, ns, transportId, "TransportManager", rootTopic);

        var status = await waitTask;
        Assert.Equal(NodeStatus.Success, status);
    }

    [Fact]
    public async Task WaitForRegistration_NamespaceHolon_DrainsBufferedMessages()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = "ManufacturingDispatcher_phuket";
        var transportId = "TransportManager_phuket";

        await using var namespaceHandle = await CreateClientAsync($"{ns}_namespace_drain");
        var namespaceClient = namespaceHandle.Client;

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = ns,
            AgentRole = "NamespaceHolon"
        };
        context.Set("config.Namespace", ns);
        context.Set("MessagingClient", namespaceClient);

        var wait = new WaitForRegistrationNode
        {
            Context = context,
            TimeoutSeconds = 3,
            ExpectedCount = 2,
            ExpectedAgents = $"{dispatchingId},{transportId}",
            ExpectedTypes = "registerMessage",
            TopicOverride = $"/{ns}/register"
        };
        wait.SetLogger(NullLogger<WaitForRegistrationNode>.Instance);

        // Subscribe and publish before executing the node so messages are buffered.
        var rootTopic = $"/{ns}/register";
        await namespaceClient.SubscribeAsync(rootTopic);
        await using var dispatchPublisher = await CreateClientAsync($"{dispatchingId}_drain_pub");
        await using var transportPublisher = await CreateClientAsync($"{transportId}_drain_pub");
        await PublishRegisterAsync(dispatchPublisher.Client, ns, dispatchingId, "Dispatching", rootTopic);
        await PublishRegisterAsync(transportPublisher.Client, ns, transportId, "TransportManager", rootTopic);
        await Task.Delay(500);

        var status = await wait.Execute();
        Assert.Equal(NodeStatus.Success, status);
    }

    [Fact]
    public async Task RegisterAgent_DispatchingAgent_PublishesRegisterToNamespaceWithSubagentsFromState()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = $"DispatchingAgent_{ns}";

        await using var senderHandle = await CreateClientAsync($"{dispatchingId}_sender");
        await using var receiverHandle = await CreateClientAsync($"{dispatchingId}_receiver");
        var sender = senderHandle.Client;
        var receiver = receiverHandle.Client;

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

        await using var senderHandle = await CreateClientAsync($"{dispatchingId}_agg_sender");
        await using var receiverHandle = await CreateClientAsync($"{dispatchingId}_agg_receiver");
        var sender = senderHandle.Client;
        var receiver = receiverHandle.Client;

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

        var summaryElements = AasValueUnwrap.UnwrapToEnumerable<ISubmodelElement>(summary!.Value);
        var freeProp = summaryElements.OfType<Property>().First(p => string.Equals(p.IdShort, "free", StringComparison.OrdinalIgnoreCase));
        var occupiedProp = summaryElements.OfType<Property>().First(p => string.Equals(p.IdShort, "occupied", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(5, AasValueUnwrap.UnwrapToInt(freeProp.Value));
        Assert.Equal(5, AasValueUnwrap.UnwrapToInt(occupiedProp.Value));
    }

    [Fact]
    public async Task RegisterAgent_DispatchingAgent_PrunesStaleModulesByTimeout()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var dispatchingId = $"DispatchingAgent_{ns}";

        await using var senderHandle = await CreateClientAsync($"{dispatchingId}_prune_sender");
        await using var receiverHandle = await CreateClientAsync($"{dispatchingId}_prune_receiver");
        var sender = senderHandle.Client;
        var receiver = receiverHandle.Client;

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

        var summaryElements = AasValueUnwrap.UnwrapToEnumerable<ISubmodelElement>(summary!.Value);
        var freeProp = summaryElements.OfType<Property>().First(p => string.Equals(p.IdShort, "free", StringComparison.OrdinalIgnoreCase));
        var occupiedProp = summaryElements.OfType<Property>().First(p => string.Equals(p.IdShort, "occupied", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, AasValueUnwrap.UnwrapToInt(freeProp.Value));
        Assert.Equal(2, AasValueUnwrap.UnwrapToInt(occupiedProp.Value));
    }

    [Fact]
    public async Task SubscribeAgentTopics_DispatchingAgent_UpdatesStateFromIncomingRegistration()
    {
        var ns = $"test{Guid.NewGuid():N}";

        await using var dispatchHandle = await CreateClientAsync($"{ns}_dispatch_state");
        await using var publisherHandle = await CreateClientAsync($"{ns}_publisher_state");
        var dispatching = dispatchHandle.Client;
        var publisher = publisherHandle.Client;

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

        await publisher.PublishAsync(msg, $"/{ns}/register");

        await Task.Delay(500);

        var state = context.Get<DispatchingState>("DispatchingState");
        Assert.NotNull(state);
        Assert.Contains(state!.Modules, m => string.Equals(m.ModuleId, "P102", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.Modules.First(m => m.ModuleId == "P102").Capabilities, c => c == "Assemble");
    }

    [Fact]
    public async Task SubscribeAgentTopics_DispatchingAgent_UpdatesInventoryFromIncomingInventoryUpdate()
    {
        var ns = $"test{Guid.NewGuid():N}";

        await using var dispatchHandle = await CreateClientAsync($"{ns}_dispatch_inventory");
        await using var publisherHandle = await CreateClientAsync($"{ns}_publisher_inventory");
        var dispatching = dispatchHandle.Client;
        var publisher = publisherHandle.Client;

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
        await Task.Delay(500);

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

        await using var dispatchHandle = await CreateClientAsync($"{ns}_dispatch_offer");
        await using var publisherHandle = await CreateClientAsync($"{ns}_publisher_offer");
        var dispatching = dispatchHandle.Client;
        var publisher = publisherHandle.Client;

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

        await publisher.PublishAsync(msg, $"/{ns}/ManufacturingSequence/Response");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(tcs.Task, completed);
        var response = await tcs.Task;
        Assert.NotNull(response);
        Assert.Equal(I40MessageTypes.PROPOSAL, response!.Frame?.Type);
        Assert.Equal(conv, response.Frame?.ConversationId);
    }

    private const string MqttBrokerHost = "192.168.178.30";
    private const int MqttBrokerPort = 1883;

    private static async Task<MqttClientHandle> CreateClientAsync(string clientIdPrefix)
    {
        var handle = new MqttClientHandle(clientIdPrefix);
        await handle.Client.ConnectAsync();
        return handle;
    }

    private static async Task PublishRegisterAsync(
        MessagingClient publisher,
        string ns,
        string agentId,
        string role,
        string? topicOverride = null)
    {
        if (publisher == null) throw new ArgumentNullException(nameof(publisher));

        var message = BuildRegisterMessage(agentId, role);

        if (!publisher.IsConnected)
        {
            await publisher.ConnectAsync();
        }

        var topic = string.IsNullOrWhiteSpace(topicOverride)
            ? $"/{ns}/{agentId}/register"
            : topicOverride;

        await publisher.PublishAsync(message, topic);
    }

    private static I40Message BuildRegisterMessage(string agentId, string role)
    {
        var register = new RegisterMessage(agentId, new(), new());
        return new I40MessageBuilder()
            .From(agentId, role)
            .To("Namespace", null)
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(register.ToSubmodelElementCollection())
            .Build();
    }

    private sealed class MqttClientHandle : IAsyncDisposable
    {
        public MessagingClient Client { get; }

        public MqttClientHandle(string clientIdPrefix)
        {
            var id = $"{clientIdPrefix}_{Guid.NewGuid():N}";
            Client = new MessagingClient(new MqttTransport(MqttBrokerHost, MqttBrokerPort, id), $"{id}/logs");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Client.IsConnected)
                {
                    await Client.DisconnectAsync();
                }
            }
            catch
            {
                // ignore cleanup failures
            }
            finally
            {
                Client.Dispose();
            }
        }
    }
}

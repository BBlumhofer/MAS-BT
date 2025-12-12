using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using BaSyx.Models.AdminShell;
using Xunit;

namespace MAS_BT.Tests;

public class ModuleMessagingIntegrationTests : IDisposable
{
    private static readonly MessageSerializer Serializer = new();
    private static readonly bool UseRealMqtt = string.Equals(Environment.GetEnvironmentVariable("MASBT_TEST_USE_REAL_MQTT"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string RealMqttHost = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_HOST") ?? "localhost";
    private static readonly int RealMqttPort = int.TryParse(Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PORT"), out var parsedPort) ? parsedPort : 1883;
    private static readonly string? RealMqttUsername = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_USERNAME");
    private static readonly string? RealMqttPassword = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PASSWORD");

    private readonly List<MessagingClient> _clients = new();

    private static string ResolveTestFile(string name)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../tests/TestFiles", name));
    }

    private static I40Message LoadMessage(string name)
    {
        var path = ResolveTestFile(name);
        var json = File.ReadAllText(path);
        return Serializer.Deserialize(json) ?? throw new InvalidOperationException($"Failed to load message {name}");
    }

    private async Task<MessagingClient> CreateClientAsync(string defaultTopic)
    {
        IMessagingTransport transport = UseRealMqtt
            ? new MqttTransport(
                RealMqttHost,
                RealMqttPort,
                $"masbt-tests-{Guid.NewGuid():N}",
                RealMqttUsername,
                RealMqttPassword)
            : new InMemoryTransport();

        var client = new MessagingClient(transport, defaultTopic);
        await client.ConnectAsync();
        _clients.Add(client);
        return client;
    }

    [Fact]
    public async Task ForwardCapabilityRequests_ForwardsDispatcherCfpToPlanningTopic()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P102";

        var moduleContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = moduleId,
            AgentRole = "ModuleHolon"
        };
        moduleContext.Set("config.Namespace", ns);
        moduleContext.Set("Namespace", ns);
        moduleContext.Set("config.Agent.AgentId", moduleId);
        moduleContext.Set("ModuleId", moduleId);
        var moduleNameAlias = "AssemblyStation";
        moduleContext.Set("config.Agent.ModuleName", moduleNameAlias);

        var moduleClient = await CreateClientAsync($"{moduleId}/logs");
        await moduleClient.SubscribeAsync($"/{ns}/DispatchingAgent/Offer");
        moduleContext.Set("MessagingClient", moduleClient);

        var planningClients = new List<(MessagingClient client, TaskCompletionSource<I40Message?> tcs)>();
        var conversationId = Guid.NewGuid().ToString();

        var primaryPlanningClient = await CreateClientAsync($"{moduleId}/planning");
        await primaryPlanningClient.SubscribeAsync($"/{ns}/{moduleId}/PlanningAgent/OfferRequest");
        var primaryTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        primaryPlanningClient.OnMessage(msg =>
        {
            if (!primaryTcs.Task.IsCompleted &&
                string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
            {
                primaryTcs.TrySetResult(msg);
            }
        });
        planningClients.Add((primaryPlanningClient, primaryTcs));

        if (!string.Equals(moduleId, moduleNameAlias, StringComparison.OrdinalIgnoreCase))
        {
            var aliasClient = await CreateClientAsync($"{moduleNameAlias}/planning");
            var aliasTopic = $"/{ns}/{moduleNameAlias}/PlanningAgent/OfferRequest";
            await aliasClient.SubscribeAsync(aliasTopic);
            var aliasTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            aliasClient.OnMessage(msg =>
            {
                if (!aliasTcs.Task.IsCompleted &&
                    string.Equals(msg.Frame?.ConversationId, conversationId, StringComparison.Ordinal))
                {
                    aliasTcs.TrySetResult(msg);
                }
            });
            planningClients.Add((aliasClient, aliasTcs));
        }

        var node = new ForwardCapabilityRequestsNode { Context = moduleContext };
        node.SetLogger(NullLogger<ForwardCapabilityRequestsNode>.Instance);

        var initialStatus = await node.Execute();
        Assert.Equal(NodeStatus.Running, initialStatus);

        var dispatcherClient = await CreateClientAsync("dispatch/logs");

        var cfpMessage = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("Broadcast", "ModuleHolon")
            .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("Drill") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-1") })
            .Build();

        await dispatcherClient.PublishAsync(cfpMessage, $"/{ns}/DispatchingAgent/Offer");

        var forwardStatus = await node.Execute();
        Assert.Equal(NodeStatus.Success, forwardStatus);

        var allTasks = Task.WhenAll(planningClients.Select(pc => pc.tcs.Task));
        var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(allTasks, completed);

        foreach (var (_, tcs) in planningClients)
        {
            var forwarded = tcs.Task.Result;
            Assert.NotNull(forwarded);
            Assert.Equal(conversationId, forwarded!.Frame?.ConversationId);
            Assert.Equal(I40MessageTypes.CALL_FOR_PROPOSAL, forwarded.Frame?.Type);
        }
    }

    [Fact]
    public async Task PlanningAgentNodes_SendProposalResponse()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var moduleId = "P102";
        var planningContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P102_Planning",
            AgentRole = "PlanningHolon"
        };
        planningContext.Set("config.Namespace", ns);
        planningContext.Set("Namespace", ns);
        planningContext.Set("config.Agent.ModuleName", moduleId);
        planningContext.Set("ModuleId", moduleId);

        var planningClient = await CreateClientAsync("planning/logs");
        planningContext.Set("MessagingClient", planningClient);

        var moduleClient = await CreateClientAsync("module/logs");
        var responseTopic = $"/{ns}/{moduleId}/PlanningAgent/OfferResponse";
        await moduleClient.SubscribeAsync(responseTopic);

        var proposalTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        moduleClient.OnMessage(msg => proposalTcs.TrySetResult(msg));

        var conversationId = Guid.NewGuid().ToString();
        var cfp = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("Broadcast", "ModuleHolon")
            .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
            .WithConversationId(conversationId)
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("Assemble") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-99") })
            .Build();

        planningContext.Set("LastReceivedMessage", cfp);

        var parseNode = new ParseCapabilityRequestNode { Context = planningContext };
        var planNode = new PlanCapabilityOfferNode { Context = planningContext };
        var sendNode = new SendCapabilityOfferNode { Context = planningContext };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Success, await planNode.Execute());
        Assert.Equal(NodeStatus.Success, await sendNode.Execute());

        var completed = await Task.WhenAny(proposalTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(proposalTcs.Task, completed);

        var proposal = proposalTcs.Task.Result;
        Assert.NotNull(proposal);
        Assert.Equal(I40MessageTypes.PROPOSAL, proposal!.Frame?.Type);
        Assert.Equal(conversationId, proposal.Frame?.ConversationId);
    }

    [Fact]
    public async Task DispatchingAgentProcessesRealProcessChainMessage()
    {
        var (offers, ctx, processChain) = await DispatchProcessChainAsync();
        Assert.NotNull(ctx);
        Assert.Equal(ctx!.Requirements.Count, offers.Count);

        foreach (var requirement in ctx.Requirements)
        {
            var match = offers.FirstOrDefault(msg => string.Equals(ExtractProperty(msg, "Capability"), requirement.Capability, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(match);
            Assert.Equal(processChain.Frame?.ConversationId, match!.Frame?.ConversationId);
            Assert.Equal(I40MessageTypes.CALL_FOR_PROPOSAL, match.Frame?.Type);
            Assert.Equal("ModuleHolon", match.Frame?.Receiver?.Role?.Name);

            if (requirement.CapabilityContainer != null)
            {
                Assert.Contains(match.InteractionElements.OfType<SubmodelElementCollection>(),
                    smc => string.Equals(smc.IdShort, requirement.CapabilityContainer.IdShort, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task PlanningAgentRespondsToDispatcherOffer()
    {
        var (offers, _, _) = await DispatchProcessChainAsync();
        Assert.NotEmpty(offers);
        var cfp = offers.First();
        var ns = "phuket";
        var moduleId = "P102";

        var planningContext = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P102_PlanningHolon",
            AgentRole = "PlanningHolon"
        };
        planningContext.Set("config.Namespace", ns);
        planningContext.Set("Namespace", ns);
        planningContext.Set("config.Agent.ModuleName", moduleId);
        planningContext.Set("ModuleId", moduleId);
        planningContext.Set("LastReceivedMessage", cfp);

        var planningClient = await CreateClientAsync("planning/logs");
        planningContext.Set("MessagingClient", planningClient);

        var moduleClient = await CreateClientAsync("module/logs");
        var responseTopic = $"/{ns}/{moduleId}/PlanningAgent/OfferResponse";
        await moduleClient.SubscribeAsync(responseTopic);

        var proposalTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        moduleClient.OnMessage(msg => proposalTcs.TrySetResult(msg));

        var parseNode = new ParseCapabilityRequestNode { Context = planningContext };
        var planNode = new PlanCapabilityOfferNode { Context = planningContext };
        var sendNode = new SendCapabilityOfferNode { Context = planningContext };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Success, await planNode.Execute());
        Assert.Equal(NodeStatus.Success, await sendNode.Execute());

        var completed = await Task.WhenAny(proposalTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(proposalTcs.Task, completed);
        var proposal = proposalTcs.Task.Result;
        Assert.NotNull(proposal);

        Assert.Equal(I40MessageTypes.PROPOSAL, proposal!.Frame?.Type);
        Assert.Equal(cfp.Frame?.ConversationId, proposal.Frame?.ConversationId);
    }

    private async Task<(List<I40Message> Offers, ProcessChainNegotiationContext Ctx, I40Message OriginalRequest)> DispatchProcessChainAsync()
    {
        var processChain = LoadMessage("ProcessChain.json");
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "DispatchingAgent_phuket",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", "phuket");
        context.Set("Namespace", "phuket");
        context.Set("LastReceivedMessage", processChain);

        var dispatchClient = await CreateClientAsync("dispatch/logs");
        context.Set("MessagingClient", dispatchClient);

        var moduleClient = await CreateClientAsync("module/logs");
        await moduleClient.SubscribeAsync("/phuket/DispatchingAgent/Offer");

        var parseNode = new ParseProcessChainRequestNode { Context = context };
        var dispatchNode = new DispatchCapabilityRequestsNode { Context = context };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        var negotiation = context.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation")
                           ?? throw new InvalidOperationException("Negotiation context missing after parse");
        var expectedCount = negotiation.Requirements.Count;
        Assert.True(expectedCount > 0, "Process chain did not contain requirements");

        var offers = new List<I40Message>();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        moduleClient.OnMessageType(I40MessageTypes.CALL_FOR_PROPOSAL, msg =>
        {
            lock (offers)
            {
                offers.Add(msg);
                if (offers.Count >= expectedCount)
                {
                    completion.TrySetResult(true);
                }
            }
        });

        Assert.Equal(NodeStatus.Success, await dispatchNode.Execute());
        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(completion.Task, completed);

        lock (offers)
        {
            return (offers.ToList(), negotiation, processChain);
        }
    }

    private static string? ExtractProperty(I40Message message, string idShort)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            if (element is Property prop && string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value?.Value?.ToString();
            }
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // ignored
            }
        }
        _clients.Clear();
    }
}

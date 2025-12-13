using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using MAS_BT.Core;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using MAS_BT.Services.Graph;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

public class ProcessChainCapabilityMatchmakingTests : IDisposable
{
    private static readonly MessageSerializer Serializer = new();
    private readonly List<MessagingClient> _clients = new();

    private static string ResolveTestFile(string name)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../tests/TestFiles", name));

    private static I40Message LoadMessage(string name)
    {
        var path = ResolveTestFile(name);
        var json = File.ReadAllText(path);
        return Serializer.Deserialize(json) ?? throw new InvalidOperationException($"Failed to load message {name}");
    }

    private async Task<MessagingClient> CreateClientAsync(string defaultTopic)
    {
        IMessagingTransport transport = new InMemoryTransport();
        var client = new MessagingClient(transport, defaultTopic);
        await client.ConnectAsync();
        _clients.Add(client);
        return client;
    }

    private sealed class FakeGraphQuery(bool result) : IGraphCapabilityQuery
    {
        public Task<bool> AnyRegisteredAgentImplementsAllAsync(
            string @namespace,
            IReadOnlyCollection<string> requiredCapabilities,
            IReadOnlyCollection<string> registeredAgentIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CapabilityCheckSuccess_AllowsDispatchOfCapabilityRequests()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var processChain = LoadMessage("ProcessChain.json");

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "DispatchingAgent_test",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("Namespace", ns);
        context.Set("LastReceivedMessage", processChain);
        context.Set("GraphCapabilityQuery", new FakeGraphQuery(true));

        // Simulate at least one registered module/agent.
        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo { ModuleId = "Module_1", Capabilities = new List<string> { "Assemble" } });
        context.Set("DispatchingState", state);

        var dispatchClient = await CreateClientAsync("dispatch/logs");
        context.Set("MessagingClient", dispatchClient);

        var moduleClient = await CreateClientAsync("module/logs");
        var offerTopic = $"/{ns}/DispatchingAgent/Offer";
        await moduleClient.SubscribeAsync(offerTopic);

        var parseNode = new ParseProcessChainRequestNode { Context = context };
        var checkNode = new CheckForCapabilitiesInNamespaceNode { Context = context };
        var dispatchNode = new DispatchCapabilityRequestsNode { Context = context };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Success, await checkNode.Execute());

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
            Assert.Equal(expectedCount, offers.Count);
        }
    }

    [Fact]
    public async Task CapabilityCheckFailure_SendsRefusalResponse()
    {
        var ns = $"test{Guid.NewGuid():N}";
        var processChain = LoadMessage("ProcessChain.json");

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "DispatchingAgent_test",
            AgentRole = "DispatchingAgent"
        };
        context.Set("config.Namespace", ns);
        context.Set("Namespace", ns);
        context.Set("LastReceivedMessage", processChain);
        context.Set("GraphCapabilityQuery", new FakeGraphQuery(false));

        var state = new DispatchingState();
        state.Upsert(new DispatchingModuleInfo { ModuleId = "Module_1", Capabilities = new List<string> { "Assemble" } });
        context.Set("DispatchingState", state);

        var dispatchClient = await CreateClientAsync("dispatch/logs");
        context.Set("MessagingClient", dispatchClient);

        var listener = await CreateClientAsync("listener/logs");
        var responseTopic = $"/{ns}/ProcessChain";
        await listener.SubscribeAsync(responseTopic);

        var responseTcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnMessage(msg =>
        {
            if (string.Equals(msg.Frame?.ConversationId, processChain.Frame?.ConversationId, StringComparison.Ordinal))
            {
                responseTcs.TrySetResult(msg);
            }
        });

        var parseNode = new ParseProcessChainRequestNode { Context = context };
        var checkNode = new CheckForCapabilitiesInNamespaceNode { Context = context };
        var sendNode = new SendProcessChainResponseNode { Context = context };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Failure, await checkNode.Execute());
        Assert.Equal(NodeStatus.Success, await sendNode.Execute());

        var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(responseTcs.Task, completed);

        var response = responseTcs.Task.Result;
        Assert.NotNull(response);
        Assert.Equal(I40MessageTypes.REFUSE_PROPOSAL, response!.Frame?.Type);

        var reason = ExtractProperty(response, "Reason");
        Assert.Equal("No registered agent implements the required capabilities", reason);
    }

    private static string? ExtractProperty(I40Message message, string idShort)
    {
        if (message?.InteractionElements == null)
        {
            return null;
        }

        foreach (var element in message.InteractionElements)
        {
            if (element is BaSyx.Models.AdminShell.Property prop &&
                string.Equals(prop.IdShort, idShort, StringComparison.OrdinalIgnoreCase))
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
            try { client.Dispose(); } catch { }
        }
        _clients.Clear();
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests
{
    public class DispatchCapabilityRequestsNodeTests
    {
        [Fact]
        public async Task Execute_PublishesCfP_ForExactMatch()
        {
            var transport = new TestHelpers.InMemoryTransport();
            var client = new MessagingClient(transport, "dispatch/logs");
            await client.ConnectAsync();

            var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.SubscribeAsync("/test/Offer");
            client.OnMessage(msg =>
            {
                if (!tcs.Task.IsCompleted && msg.Frame?.Type != null && msg.Frame.Type.StartsWith(I40MessageTypes.CALL_FOR_PROPOSAL))
                {
                    tcs.TrySetResult(msg);
                }
            });

            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "DispatchingAgent_tests",
                AgentRole = "DispatchingAgent"
            };
            ctx.Set("config.Namespace", "test");
            ctx.Set("config.DispatchingAgent.SimilarityAgentId", "SimilarityAnalysisAgent_test");
            ctx.Set("MessagingClient", client);

            // seed dispatching state with a module capable of 'Drill'
            var state = new DispatchingState();
            state.Upsert(new DispatchingModuleInfo { ModuleId = "P101", Capabilities = new System.Collections.Generic.List<string> { "Drill" } });
            state.Upsert(new DispatchingModuleInfo { ModuleId = "SimilarityAnalysisAgent_test", Capabilities = new System.Collections.Generic.List<string>() });
            state.SetCapabilityDescription("Drill", "Drill description");
            state.SetCapabilitySimilarity("Drill", "Drill", 1.0);
            ctx.Set("DispatchingState", state);

            var negotiation = new ProcessChainNegotiationContext();
            negotiation.ConversationId = Guid.NewGuid().ToString();
            negotiation.Requirements.Add("Drill");
            ctx.Set("ProcessChain.Negotiation", negotiation);

            var node = new DispatchCapabilityRequestsNode { Context = ctx };
            node.SetLogger(NullLogger<DispatchCapabilityRequestsNode>.Instance);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(tcs.Task, completed);
            var msg = await tcs.Task;
            Assert.NotNull(msg);
            Assert.True(msg!.InteractionElements.Any(e => string.Equals(e.IdShort, "Capability", StringComparison.OrdinalIgnoreCase)) ||
                        msg.InteractionElements.Any());
        }

        [Fact]
        public async Task Execute_PublishesCfP_WhenSimilarityDisabledByConfig()
        {
            var transport = new TestHelpers.InMemoryTransport();
            var client = new MessagingClient(transport, "dispatch/logs");
            await client.ConnectAsync();

            var tcs = new TaskCompletionSource<I40Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.SubscribeAsync("/test/Offer");
            client.OnMessage(msg =>
            {
                if (!tcs.Task.IsCompleted && msg.Frame?.Type != null && msg.Frame.Type.StartsWith(I40MessageTypes.CALL_FOR_PROPOSAL))
                {
                    tcs.TrySetResult(msg);
                }
            });

            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "DispatchingAgent_tests",
                AgentRole = "DispatchingAgent"
            };
            ctx.Set("config.Namespace", "test");
            ctx.Set("config.DispatchingAgent.UseSimilarityFiltering", false);
            ctx.Set("config.DispatchingAgent.SimilarityAgentId", "SimilarityAnalysisAgent_test");
            ctx.Set("MessagingClient", client);

            var state = new DispatchingState();
            state.Upsert(new DispatchingModuleInfo { ModuleId = "P101", Capabilities = new System.Collections.Generic.List<string> { "Drill" } });
            // Similarity agent not required when UseSimilarityFiltering=false.
            ctx.Set("DispatchingState", state);

            var negotiation = new ProcessChainNegotiationContext();
            negotiation.ConversationId = Guid.NewGuid().ToString();
            negotiation.Requirements.Add("Drill");
            ctx.Set("ProcessChain.Negotiation", negotiation);

            var node = new DispatchCapabilityRequestsNode { Context = ctx };
            node.SetLogger(NullLogger<DispatchCapabilityRequestsNode>.Instance);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(tcs.Task, completed);
            Assert.NotNull(await tcs.Task);
        }
    }
}

using System;
using System.Threading.Tasks;
using AasSharpClient.Models.ProcessChain;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Core;
using MAS_BT.Core;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests
{
    public class ParseProcessChainRequestNodeTests
    {
        [Fact]
        public async Task Execute_WithProcessChain_SetsNegotiationContext()
        {
            var msg = new I40MessageBuilder()
                .From("sender", "Requester")
                .To("DispatchingAgent", "DispatchingAgent")
                .WithType("callForProposal")
                .WithConversationId(Guid.NewGuid().ToString())
                .Build();

            var pc = new ProcessChain();
            var req1 = new RequiredCapability("RequiredCapability_Drill");
            // Add a Capability element so ParseProcessChainRequestNode recognizes this container
            req1.Add(new BaSyx.Models.AdminShell.Capability("Drill"));
            req1.SetInstanceIdentifier("req-drill");
            req1.SetRequiredCapabilityReference(new BaSyx.Models.AdminShell.Reference(new System.Collections.Generic.List<BaSyx.Models.AdminShell.IKey>{ new BaSyx.Models.AdminShell.Key(BaSyx.Models.AdminShell.KeyType.GlobalReference, "capability:Drill")}){ Type = BaSyx.Models.AdminShell.ReferenceType.ExternalReference });
            pc.AddRequiredCapability(req1);

            msg.InteractionElements.Add(pc);

            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "DispatchingAgent_tests",
                AgentRole = "DispatchingAgent"
            };
            ctx.Set("LastReceivedMessage", msg);

            var node = new ParseProcessChainRequestNode { Context = ctx };
            node.SetLogger(NullLogger<ParseProcessChainRequestNode>.Instance);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var negotiation = ctx.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
            Assert.NotNull(negotiation);
            Assert.True(negotiation.Requirements.Count > 0);
        }

        [Fact]
        public async Task Execute_WithProductId_RewritesConversationId()
        {
            var originalConversationId = Guid.NewGuid().ToString();
            var expectedProductId = "product-P100-001";

            var msg = new I40MessageBuilder()
                .From("sender", "Requester")
                .To("DispatchingAgent", "DispatchingAgent")
                .WithType("callForProposal")
                .WithConversationId(originalConversationId)
                .AddElement(new BaSyx.Models.AdminShell.Property<string>("ProductId")
                {
                    Value = new BaSyx.Models.AdminShell.PropertyValue<string>(expectedProductId)
                })
                .Build();

            var pc = new ProcessChain();
            var requirement = new RequiredCapability("RequiredCapability_Transport");
            requirement.Add(new BaSyx.Models.AdminShell.Capability("Transport"));
            requirement.SetInstanceIdentifier("req-transport");
            requirement.SetRequiredCapabilityReference(new BaSyx.Models.AdminShell.Reference(new System.Collections.Generic.List<BaSyx.Models.AdminShell.IKey>
            {
                new BaSyx.Models.AdminShell.Key(BaSyx.Models.AdminShell.KeyType.GlobalReference, "capability:Transport")
            })
            {
                Type = BaSyx.Models.AdminShell.ReferenceType.ExternalReference
            });
            pc.AddRequiredCapability(requirement);
            msg.InteractionElements.Add(pc);

            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "DispatchingAgent_tests",
                AgentRole = "DispatchingAgent"
            };
            ctx.Set("LastReceivedMessage", msg);

            var node = new ParseProcessChainRequestNode { Context = ctx };
            node.SetLogger(NullLogger<ParseProcessChainRequestNode>.Instance);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var negotiation = ctx.Get<ProcessChainNegotiationContext>("ProcessChain.Negotiation");
            Assert.NotNull(negotiation);
            Assert.Equal(expectedProductId, negotiation!.ProductId);
            Assert.Equal(expectedProductId, negotiation.ConversationId);
            Assert.Equal(expectedProductId, ctx.Get<string>("ConversationId"));
        }
    }
}

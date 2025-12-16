using System;
using System.Linq;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests
{
    public class BuildCreateDescriptionResponseNodeTests
    {
        [Fact]
        public async Task Execute_NoRequestMessage_ReturnsFailure()
        {
            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "TestAgent"
            };

            var node = new BuildCreateDescriptionResponseNode { Context = ctx };
            node.SetLogger(NullLogger<BuildCreateDescriptionResponseNode>.Instance);

            var status = await node.Execute();

            Assert.Equal(NodeStatus.Failure, status);
        }

        [Fact]
        public async Task Execute_WithRequestMessage_CreatesResponseMessage()
        {
            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "TestAgent"
            };

            var conversationId = Guid.NewGuid().ToString();
            var request = new I40MessageBuilder()
                .From("requester-id", "Requester")
                .To("TestAgent", "AIAgent")
                .WithType("createDescription")
                .WithConversationId(conversationId)
                .Build();

            ctx.Set("CreateDescriptionTargetMessage", request);
            ctx.Set("Description_Result", "meine Beschreibung");

            var node = new BuildCreateDescriptionResponseNode { Context = ctx };
            node.SetLogger(NullLogger<BuildCreateDescriptionResponseNode>.Instance);

            var status = await node.Execute();
            Assert.Equal(NodeStatus.Success, status);

            var resp = ctx.Get<I40Message>("ResponseMessage");
            Assert.NotNull(resp);
            Assert.Equal("informConfirm", resp.Frame?.Type);
            Assert.Equal(conversationId, resp.Frame?.ConversationId);

            var elem = resp.InteractionElements.FirstOrDefault(e => string.Equals(e.IdShort, "Description_Result", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(elem);
        }
    }
}

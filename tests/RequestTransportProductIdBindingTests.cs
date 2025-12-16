using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.Messages;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning;
using MAS_BT.Nodes.Planning.ProcessChain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests
{
    public class RequestTransportProductIdBindingTests
    {
        [Fact]
        public void BuildTransportRequest_ResolvesProductIdFromModuleInventory_WhenWildcard()
        {
            // Arrange
            var ctx = new BTContext(NullLogger<BTContext>.Instance)
            {
                AgentId = "P100",
                AgentRole = "PlanningAgent"
            };

            var node = new RequestTransportNode();
            node.Initialize(ctx, NullLogger<RequestTransportNode>.Instance);

            // prepare CapabilityRequestContext without ProductId
            var req = new CapabilityRequestContext
            {
                RequirementId = "req1",
                ProductId = string.Empty
            };

            // prepare TransportRequirement with wildcard placeholder
            var tr = new TransportRequirement
            {
                Target = "StorageA",
                ProductIdPlaceholder = "*"
            };

            // prepare ModuleInventory in context
            var storage = new StorageUnit
            {
                Name = "StorageA",
                Slots = new List<Slot>
                {
                    new Slot { Index = 0, Content = new SlotContent { ProductID = "PROD-123" } }
                }
            };

            ctx.Set("ModuleInventory", new List<StorageUnit> { storage });

            // Act: invoke private BuildTransportRequestElement via reflection
            var mi = typeof(RequestTransportNode).GetMethod("BuildTransportRequestElement", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            var element = mi.Invoke(node, new object?[] { req, "StorageA", tr, 0 }) as TransportRequestMessage;

            // Assert
            Assert.NotNull(element);
            var idVal = AasValueUnwrap.UnwrapToString(element.IdentifierValue.Value);
            Assert.Equal("PROD-123", idVal);
            Assert.Equal("PROD-123", tr.ResolvedProductId);
        }
    }
}

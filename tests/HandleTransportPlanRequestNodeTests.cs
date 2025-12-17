using System;
using System.Linq;
using System.Reflection;
using AasSharpClient.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Tests;

public class HandleTransportPlanRequestNodeTests
{
    [Fact]
    public void BuildTransportOffer_StoresIdentifierInValue_WithValidKey()
    {
        var ctx = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "DispatchingAgent_tests",
            AgentRole = "DispatchingAgent"
        };

        var node = new HandleTransportPlanRequestNode { Context = ctx };
        node.SetLogger(NullLogger<HandleTransportPlanRequestNode>.Instance);

        var mi = typeof(HandleTransportPlanRequestNode)
            .GetMethod("BuildTransportOffer", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(mi);

        var offer = mi!.Invoke(node, new object?[] { "Storage", "https://example.test/product/123", "ProductId" }) as AasSharpClient.Models.ProcessChain.OfferedCapability;
        Assert.NotNull(offer);

        var action = offer!.Actions.OfType<ActionModel>().Single();

        Assert.NotNull(action.InputParameters);
        Assert.True(action.InputParameters.TryGetParameterValue<string>("ProductID", out var stored));
        Assert.Equal("https://example.test/product/123", stored);

        // Ensure we did not emit an invalid idShort key that could be dropped during serialization.
        Assert.DoesNotContain(action.InputParameters.Parameters.Keys, k => k.Contains(":") || k.Contains(" "));
    }
}

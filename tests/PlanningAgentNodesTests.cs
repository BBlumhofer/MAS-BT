using System.Threading.Tasks;
using AasSharpClient.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning;
using Xunit;

namespace MAS_BT.Tests;

public class PlanningAgentNodesTests
{
    [Fact]
    public async Task LoadProductionPlan_CreatesPlan()
    {
        var ctx = new BTContext();
        var node = new LoadProductionPlanNode { DefaultMachine = "ModuleX", Context = ctx };
        var status = await node.Execute();
        Assert.Equal(NodeStatus.Success, status);
        var plan = ctx.Get<ProductionPlan>("ProductionPlan");
        Assert.NotNull(plan);
    }

    [Fact]
    public async Task SelectNextAction_FailsWithoutPlan()
    {
        var ctx = new BTContext();
        var node = new SelectNextActionNode { Context = ctx };
        var status = await node.Execute();
        Assert.Equal(NodeStatus.Failure, status);
    }
}

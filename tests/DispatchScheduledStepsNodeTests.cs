using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

public class DispatchScheduledStepsNodeTests
{
    [Fact]
    public async Task DispatchRespectsSchedulingWindows()
    {
        var plan = LoadPlanWithOffsets(out var anchor, out var firstStart, out var secondStart);

        var ctx = new BTContext();
        ctx.Set("ProductionPlan", plan);
        ctx.Set("OfferStatus", "booked");
        ctx.Set("TransportAccepted", true);
        ctx.Set("MachineReady", true);
        ctx.Set("ResourceAvailable", true);

        var select = InitNode(new SelectNextActionNode { Context = ctx });
        await select.Execute();

        var dispatch = InitNode(new DispatchScheduledStepsNode { Context = ctx });

        ctx.Set("SchedulingReferenceTimeUtc", anchor.AddSeconds(5));
        await dispatch.Execute();
        Assert.False(ctx.Get<bool?>("DispatchReady") == true);

        ctx.Set("DispatchReady", false);
        ctx.Set("SchedulingReferenceTimeUtc", firstStart.AddSeconds(1));
        await dispatch.Execute();
        Assert.True(ctx.Get<bool?>("DispatchReady"));

        ctx.Set("DispatchReady", false);
        var secondStep = plan.Steps[1];
        ctx.Set("CurrentPlanStep", secondStep);
        ctx.Set("CurrentPlanAction", secondStep.Actions.First());

        ctx.Set("SchedulingReferenceTimeUtc", firstStart.AddSeconds(1));
        await dispatch.Execute();
        Assert.False(ctx.Get<bool?>("DispatchReady") == true);

        ctx.Set("DispatchReady", false);
        ctx.Set("SchedulingReferenceTimeUtc", secondStart.AddSeconds(1));
        await dispatch.Execute();
        Assert.True(ctx.Get<bool?>("DispatchReady"));
    }

    private static T InitNode<T>(T node) where T : BTNode
    {
        node.SetLogger(NullLogger.Instance);
        return node;
    }

    private static ProductionPlan LoadPlanWithOffsets(out DateTime anchor, out DateTime firstStart, out DateTime secondStart)
    {
        anchor = DateTime.UtcNow;
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var planPath = Path.Combine(projectRoot, "tests", "TestFiles", "ProductionPlan.json");
        var json = File.ReadAllText(planPath);
        var plan = ProductionPlan.Parse(json);
        plan.FillSteps();

        firstStart = anchor.AddSeconds(20);
        secondStart = anchor.AddSeconds(50);
        var duration = TimeSpan.FromSeconds(15);

        ApplySchedule(plan.Steps[0].Scheduling, firstStart, duration);
        ApplySchedule(plan.Steps[1].Scheduling, secondStart, duration);

        return plan;
    }

    private static void ApplySchedule(SchedulingContainer scheduling, DateTime start, TimeSpan duration)
    {
        scheduling.SetStartDateTime(start);
        scheduling.SetEndDateTime(start.Add(duration));
        scheduling.SetCycleTime(duration);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace MAS_BT.Nodes.Planning;

/// <summary>
/// LoadProductionPlan - Provides a minimal ProductionPlan in context if none is present.
/// This is a stub for planning; replace with real AAS loading later.
/// </summary>
public class LoadProductionPlanNode : BTNode
{
    public string PlanId { get; set; } = "Plan001";
    public string DefaultMachine { get; set; } = "CA-Module";

    public LoadProductionPlanNode() : base("LoadProductionPlan")
    {
    }

    public override Task<NodeStatus> Execute()
    {
        var plan = Context.Get<ProductionPlan>("ProductionPlan");
        if (plan != null)
        {
            Logger.LogInformation("LoadProductionPlan: Plan already present in context (steps: {Count})", plan.Steps.Count);
            return Task.FromResult(NodeStatus.Success);
        }

        // Create a simple plan with two steps (Store + Retrieve) as placeholder
        // Step 1: Store
        var inputParameters1 = new InputParameters();
        inputParameters1.SetParameter("ProductId", "https://smartfactory.de/shells/test_product_step");

        var skillRef = new SkillReference(new List<(object Key, string Value)>
        {
            ((object)ModelReferenceEnum.Submodel, "https://example.com/sm")
        });

        var action1 = new AasSharpClient.Models.Action("Action001", "Store", ActionStatusEnum.OPEN, inputParameters1, new FinalResultData(), new Preconditions(), skillRef, DefaultMachine);
        var scheduling1 = new SchedulingContainer(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), DateTime.UtcNow.AddSeconds(30).ToString("yyyy-MM-dd HH:mm:ss"), "00:00:00", "00:01:00");
        var step1 = new Step("Step0001", "Store", StepStatusEnum.OPEN, action1, DefaultMachine, scheduling1, "SmartFactory-KL", "_PHUKET");

        // Step 2: Retrieve
        var inputParameters2 = new InputParameters();
        inputParameters2.SetParameter("ProductId", "https://smartfactory.de/shells/test_product_step");
        inputParameters2.SetParameter("RetrieveByProductID", "true");

        var storagePre = new StoragePrecondition("Condition_001", StorageConditionContentType.ProductId, "https://smartfactory.de/shells/test_product_step");
        var preconditions = new Preconditions(new[] { storagePre });

        var action2 = new AasSharpClient.Models.Action("Action001", "Retrieve", ActionStatusEnum.OPEN, inputParameters2, new FinalResultData(), preconditions, skillRef, DefaultMachine);
        var scheduling2 = new SchedulingContainer(DateTime.UtcNow.AddSeconds(50).ToString("yyyy-MM-dd HH:mm:ss"), DateTime.UtcNow.AddSeconds(20).ToString("yyyy-MM-dd HH:mm:ss"), "00:00:00", "00:00:50");
        var step2 = new Step("Step0002", "Retrieve", StepStatusEnum.OPEN, action2, DefaultMachine, scheduling2, "SmartFactory-KL", "_PHUKET");

        var productionPlan = new ProductionPlan(false, 2, step1)
        {
            IdShort = PlanId
        };

        productionPlan.append_step(step2);

        Context.Set("ProductionPlan", productionPlan);
        Logger.LogInformation("LoadProductionPlan: Created demo plan with 2 steps/actions targeting {Machine}", DefaultMachine);
        return Task.FromResult(NodeStatus.Success);
    }
}

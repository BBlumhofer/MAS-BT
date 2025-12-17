using System.Collections.Generic;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning;
using MAS_BT.Nodes.Planning.ProcessChain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

public class CheckConstraintsProductIdPlaceholderResolutionTests
{
    [Fact]
    public async Task CheckConstraints_ResolvesWildcardProductIdFromModuleInventory_AndMutatesConstraint()
    {
        // Arrange
        var ctx = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P100",
            AgentRole = "PlanningAgent"
        };

        // ModuleInventory snapshot
        ctx.Set(
            "ModuleInventory",
            new List<StorageUnit>
            {
                new()
                {
                    Name = "StorageA",
                    Slots = new List<Slot>
                    {
                        new() { Index = 0, Content = new SlotContent { ProductID = "PROD-123" } }
                    }
                }
            });

        // Capability request with a StorageConstraint containing ProductId='*'
        var request = new CapabilityRequestContext
        {
            RequirementId = "req1",
            Capability = "Assemble",
            ProductId = string.Empty,
        };

        // Build a real CapabilityContainer with a ConstraintSet via the AAS-Sharp definition factory.
        var containerDef = new CapabilityContainerDefinition(
            IdShort: "AssembleContainer",
            Capability: new CapabilityElementDefinition("Assemble"),
            Relations: new CapabilityRelationsDefinition(
                IdShort: "Relations",
                Relationships: new List<RelationshipElementDefinition>(),
                ConstraintSet: new CapabilityConstraintSetDefinition(
                    IdShort: "ConstraintSet",
                    ConstraintContainers: new List<PropertyConstraintContainerDefinition>
                    {
                        new(
                            IdShort: "StorageConstraint",
                            ConditionalType: new PropertyValueDefinition("ConditionalType", "Pre"),
                            ConstraintType: new PropertyValueDefinition("ConstraintType", "CustomConstraint"),
                            CustomConstraint: new CustomConstraintDefinition(
                                IdShort: "CustomConstraint",
                                Properties: new List<PropertyValueDefinition>
                                {
                                    new("ConstraintName", "StorageConstraint"),
                                    new("TargetStation", "StorageA"),
                                    new("ProductId", "*")
                                }))
                    })));

        request.CapabilityContainer = CapabilityContainer.FromDefinition(containerDef);
        ctx.Set("Planning.CapabilityRequest", request);

        var node = new CheckConstraintsNode();
        node.Initialize(ctx, NullLogger<CheckConstraintsNode>.Instance);

        // Act
        var status = await node.Execute();

        // Assert
        Assert.Equal(NodeStatus.Success, status);

        var reqOut = ctx.Get<CapabilityRequestContext>("Planning.CapabilityRequest");
        Assert.NotNull(reqOut);
        Assert.Equal("PROD-123", reqOut!.ProductId);

        var requirements = ctx.Get<List<TransportRequirement>>("Planning.TransportRequirements");
        Assert.NotNull(requirements);
        Assert.Single(requirements!);
        Assert.Equal("PROD-123", requirements![0].ProductIdPlaceholder);
        Assert.Equal("PROD-123", requirements![0].ResolvedProductId);

        // Constraint should be mutated as well (best-effort behavior)
        var mutated = reqOut.CapabilityContainer!.Constraints
            .First()
            .CustomConstraint!
            .GetProperty("ProductId")
            ?.GetText();
        Assert.Equal("PROD-123", mutated);
    }
}

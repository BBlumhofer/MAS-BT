using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models;
using AasSharpClient.Models.Helpers;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Services.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Tests;

public class PlanCapabilityOfferNodeTests
{
    private static readonly MessageSerializer Serializer = new();
    private const string CapabilityRelationsRealizedBySemanticId = "https://admin-shell.io/idta/CapabilityDescription/CapabilityRelations/RealizedBy/1/0";

    [Fact]
    public async Task OfferRequestP102Assemble_PopulatesConstraintsSkillAndInputs()
    {
        var context = CreatePlanningContext();
        var request = LoadMessage("OfferRequestP102_Assemble.json");
        context.Set("LastReceivedMessage", request);

        var parseNode = new ParseCapabilityRequestNode { Context = context };
        var planNode = new PlanCapabilityOfferNode { Context = context };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());

        var status = await planNode.Execute();
        Assert.Equal(NodeStatus.Success, status);

        var plan = context.Get<CapabilityOfferPlan>("Planning.CapabilityOffer");
        Assert.NotNull(plan);
        Assert.NotNull(plan!.OfferedCapability);
        Assert.NotNull(plan.OfferedCapability!.OfferedCapabilityReference.GetReference());

        var action = plan.OfferedCapability.Actions.OfType<ActionModel>().Single();

        var conditionValues = action.Preconditions
            .OfType<SubmodelElementCollection>()
            .ToList();

        Assert.Single(conditionValues);
        var conditionValue = conditionValues.Single();
        Assert.Equal("ConditionValue_001", conditionValue.IdShort);

        var conditionProperties = conditionValue
            .OfType<Property>()
            .ToDictionary(prop => prop.IdShort ?? string.Empty, prop => prop.GetText() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("StorageConstraint", conditionProperties["ConstraintName"]);
        Assert.Equal("*", conditionProperties["ProductId"]);

        Assert.Equal(2, action.InputParameters.Parameters.Count);
        Assert.True(action.InputParameters.Parameters.ContainsKey("GripForce"));
        Assert.True(action.InputParameters.Parameters.ContainsKey("ProductWeight"));

        var skillReference = AasValueUnwrap.Unwrap(action.SkillReference.Value) as IReference;
        Assert.NotNull(skillReference);
        Assert.Contains(skillReference!.Keys, key => !string.Equals(key.Value, "EMPTY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OfferRequestP102Assemble_UsesNeo4jReference_WhenAvailable()
    {
        var context = CreatePlanningContext();
        var request = LoadMessage("OfferRequestP102_Assemble.json");
        context.Set("LastReceivedMessage", request);

        // Provide reference from Neo4j as JSON (keys array) like in prod.
        var neo4jRefJson = "[{\"type\": \"Submodel\", \"value\": \"https://smartfactory.de/submodels/capability/3b3c7ec0-ddd0-422b-99c3-7e61aa670f83\"}, {\"type\": \"SubmodelElementCollection\", \"value\": \"CapabilitySet\"}, {\"type\": \"SubmodelElementCollection\", \"value\": \"AssembleContainer\"}, {\"type\": \"Capability\", \"value\": \"Assemble\"}]";
        context.Set("CapabilityReferenceQuery", new FakeCapabilityReferenceQuery(neo4jRefJson));

        var parseNode = new ParseCapabilityRequestNode { Context = context };
        var planNode = new PlanCapabilityOfferNode { Context = context };

        Assert.Equal(NodeStatus.Success, await parseNode.Execute());
        Assert.Equal(NodeStatus.Success, await planNode.Execute());

        var plan = context.Get<CapabilityOfferPlan>("Planning.CapabilityOffer");
        Assert.NotNull(plan);
        var offered = plan!.OfferedCapability;
        Assert.NotNull(offered);

        var reference = offered!.OfferedCapabilityReference.GetReference();
        Assert.NotNull(reference);
        Assert.Equal(ReferenceType.ModelReference, reference!.Type);

        var keys = reference.Keys?.ToList() ?? new System.Collections.Generic.List<IKey>();
        Assert.Equal(4, keys.Count);
        Assert.Equal(KeyType.Submodel, keys[0].Type);
        Assert.Equal("https://smartfactory.de/submodels/capability/3b3c7ec0-ddd0-422b-99c3-7e61aa670f83", keys[0].Value);
        Assert.Equal(KeyType.SubmodelElementCollection, keys[1].Type);
        Assert.Equal("CapabilitySet", keys[1].Value);
        Assert.Equal(KeyType.SubmodelElementCollection, keys[2].Type);
        Assert.Equal("AssembleContainer", keys[2].Value);
        Assert.Equal(KeyType.Capability, keys[3].Type);
        Assert.Equal("Assemble", keys[3].Value);
    }

    [Fact]
    public async Task PlanCapabilityOffer_AppendsTransportSequenceInPlacementOrder()
    {
        var context = CreatePlanningContext();
        var request = LoadMessage("OfferRequestP102_Assemble.json");
        context.Set("LastReceivedMessage", request);

        var parseNode = new ParseCapabilityRequestNode { Context = context };
        Assert.Equal(NodeStatus.Success, await parseNode.Execute());

        var beforeTransport = CreateTransportOffer("transport_before");
        var afterTransport = CreateTransportOffer("transport_after");

        context.Set("Planning.TransportSequence", new List<TransportSequenceItem>
        {
            new TransportSequenceItem(TransportPlacement.BeforeCapability, beforeTransport),
            new TransportSequenceItem(TransportPlacement.AfterCapability, afterTransport)
        });

        var planNode = new PlanCapabilityOfferNode { Context = context };
        Assert.Equal(NodeStatus.Success, await planNode.Execute());

        var plan = context.Get<CapabilityOfferPlan>("Planning.CapabilityOffer");
        Assert.NotNull(plan);
        var offeredCapability = plan!.OfferedCapability;
        Assert.NotNull(offeredCapability);

        var sequence = offeredCapability!.CapabilitySequence.OfType<OfferedCapability>().ToList();
        Assert.Equal(2, sequence.Count);

        Assert.Equal("transport_before", sequence[0].InstanceIdentifier.GetText());
        Assert.Equal("pre", sequence[0].SequencePlacement.GetText());

        Assert.Equal("transport_after", sequence[1].InstanceIdentifier.GetText());
        Assert.Equal("post", sequence[1].SequencePlacement.GetText());

        Assert.Equal(2, plan.SupplementalCapabilities.Count);
    }

    private static BTContext CreatePlanningContext()
    {
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P102_PlanningHolon",
            AgentRole = "PlanningHolon"
        };

        context.Set("config.Namespace", "phuket");
        context.Set("Namespace", "phuket");
        context.Set("config.Agent.ModuleName", "P102");
        context.Set("ModuleId", "P102");

        var capabilityDescription = BuildCapabilityDescriptionSubmodel();
        context.Set("CapabilityDescriptionSubmodel", capabilityDescription);

        return context;
    }

    private static CapabilityDescriptionSubmodel BuildCapabilityDescriptionSubmodel()
    {
        const string capabilitySubmodelId = "urn:masbt:capability:p102";
        var submodel = CapabilityDescriptionSubmodel.CreateWithIdentifier(capabilitySubmodelId);

        var capabilityDefinition = new CapabilityContainerDefinition(
            "AssembleContainer",
            new CapabilityElementDefinition("Assemble"),
            Relations: BuildRelationsDefinition(capabilitySubmodelId),
            PropertySet: BuildPropertySetDefinition());

        submodel.AddCapabilityContainer(capabilityDefinition);
        return submodel;
    }

    private static CapabilityRelationsDefinition BuildRelationsDefinition(string capabilitySubmodelId)
    {
        var capabilityReference = CreateModelReference(
            (KeyType.Submodel, capabilitySubmodelId),
            (KeyType.SubmodelElementCollection, "CapabilitySet"),
            (KeyType.SubmodelElementCollection, "AssembleContainer"),
            (KeyType.Capability, "Assemble"));

        var skillReference = CreateModelReference(
            (KeyType.Submodel, "urn:skill:p102"),
            (KeyType.SubmodelElementCollection, "SkillSet"),
            (KeyType.SubmodelElementCollection, "AssembleSkill"));

        var realizedBy = new RelationshipElementDefinition(
            "RealizedBy",
            capabilityReference,
            skillReference,
            SemanticId: CreateExternalReference(CapabilityRelationsRealizedBySemanticId));

        var storageConstraint = new PropertyConstraintContainerDefinition(
            "StorageConstraint",
            new PropertyValueDefinition("ConditionalType", "Pre"),
            new PropertyValueDefinition("ConstraintType", "CustomConstraint"),
            new CustomConstraintDefinition(
                "CustomConstraint",
                new[]
                {
                    new PropertyValueDefinition("ConstraintName", "StorageConstraint"),
                    new PropertyValueDefinition("ProductId", "*")
                }));

        return new CapabilityRelationsDefinition(
            "Relations",
            new[] { realizedBy },
            ConstraintSet: new CapabilityConstraintSetDefinition("ConstraintSet", new[] { storageConstraint }));
    }

    private static CapabilityPropertySetDefinition BuildPropertySetDefinition()
    {
        return new CapabilityPropertySetDefinition(
            "PropertySet",
            new CapabilityPropertyContainerDefinition[]
            {
                new RangePropertyContainerDefinition("GripForceRange", "GripForce", "10", "50", "xs:double"),
                new RangePropertyContainerDefinition("ProductWeightRange", "ProductWeight", "0.5", "5", "xs:double"),
                new PropertyValueContainerDefinition("ProductIdFixed", "ProductId", "*", "xs:string")
            });
    }

    private static I40Message LoadMessage(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../tests/TestFiles", fileName));
        var raw = File.ReadAllText(path);
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            var firstBrace = raw.IndexOf('{');
            trimmed = firstBrace >= 0 ? raw[firstBrace..] : raw;
        }

        var json = trimmed.TrimStart();
        return Serializer.Deserialize(json) ?? throw new InvalidOperationException($"Failed to load {fileName}");
    }

    private static Reference CreateModelReference(params (KeyType Type, string Value)[] keys)
    {
        var keyList = keys
            .Select(tuple => (IKey)new Key(tuple.Type, tuple.Value))
            .ToList();

        return new Reference(keyList)
        {
            Type = ReferenceType.ModelReference
        };
    }

    private static Reference CreateExternalReference(string value)
    {
        var key = new Key(KeyType.GlobalReference, value);
        return new Reference(new[] { (IKey)key })
        {
            Type = ReferenceType.ExternalReference
        };
    }

    private static OfferedCapability CreateTransportOffer(string instanceId)
    {
        var offer = new OfferedCapability($"Transport_{instanceId}");
        offer.InstanceIdentifier.Value = new PropertyValue<string>(instanceId);
        offer.Station.Value = new PropertyValue<string>("Storage");
        offer.MatchingScore.Value = new PropertyValue<double>(1.0);
        offer.SetCost(0);
        return offer;
    }

    private sealed class FakeCapabilityReferenceQuery : ICapabilityReferenceQuery
    {
        private readonly string _json;

        public FakeCapabilityReferenceQuery(string json)
        {
            _json = json;
        }

        public Task<string?> GetCapabilityReferenceJsonAsync(string moduleShellId, string capabilityIdShort, System.Threading.CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(_json);
        }
    }
}

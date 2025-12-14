using System;
using System.Threading;
using System.Threading.Tasks;
using AasSharpClient.Models;
using MAS_BT.Core;
using MAS_BT.Nodes.Planning;
using MAS_BT.Nodes.Planning.ProcessChain;
using MAS_BT.Services.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

public class CapabilityMatchmakingNodeTests
{
    [Fact]
    public async Task ExactIdShortMatchSucceeds()
    {
        var capabilityName = "Drill";
        var requirement = BuildValueContainer("TorqueRequirement", "Torque", "45", "xs:double");
        var offered = BuildValueContainer("TorqueOffered", "Torque", "45", "xs:double");

        var context = CreateContext(capabilityName, requirement, offered);
        var node = CreateNode(context);

        var status = await node.Execute();

        Assert.Equal(NodeStatus.Success, status);
        Assert.Equal(capabilityName, context.Get<string>("MatchedCapability"));
    }

    [Fact]
    public async Task EmbeddingFallbackMatchesDifferentNames()
    {
        var capabilityName = "Screw";
        var requirement = BuildValueContainer("TorqueRequirement", "Torque", "21", "xs:double");
        var offered = BuildValueContainer("TorqueAlias", "ScrewTorque", "21", "xs:double");

        var context = CreateContext(capabilityName, requirement, offered);
        var node = CreateNode(context);
        node.SimilarityThreshold = 0.75;

        var status = await node.Execute();

        Assert.Equal(NodeStatus.Success, status);
        Assert.Equal(capabilityName, context.Get<string>("MatchedCapability"));
    }

    [Fact]
    public async Task WildcardOfferedPropertyAcceptsAnyValue()
    {
        var capabilityName = "Assemble";
        var requirement = BuildValueContainer("ProductRequirement", "ProductId", "ABC-123", "xs:string");
        var wildcard = BuildValueContainer("ProductWildcard", "ProductId", "*", "xs:string");

        var context = CreateContext(capabilityName, requirement, wildcard);
        var node = CreateNode(context);

        var status = await node.Execute();

        Assert.Equal(NodeStatus.Success, status);
    }

    [Fact]
    public async Task MissingMatchFails()
    {
        var capabilityName = "Mill";
        var requirement = BuildValueContainer("SpeedRequirement", "Speed", "12", "xs:double");
        var offered = BuildValueContainer("TorqueOffered", "Torque", "25", "xs:double");

        var context = CreateContext(capabilityName, requirement, offered);
        var node = CreateNode(context);
        node.RefusalReason = "no_match";
        node.SimilarityThreshold = 0.95;

        var status = await node.Execute();

        Assert.Equal(NodeStatus.Failure, status);
        Assert.Equal("no_match", context.Get<string>("RefusalReason"));
    }

    private static BTContext CreateContext(
        string capabilityName,
        CapabilityPropertyContainerDefinition requestProperty,
        CapabilityPropertyContainerDefinition offeredProperty)
    {
        var requestContext = new CapabilityRequestContext
        {
            Capability = capabilityName,
            CapabilityContainer = CapabilityContainer.FromDefinition(
                BuildCapabilityDefinition(capabilityName, requestProperty)),
            ConversationId = Guid.NewGuid().ToString(),
            RequirementId = Guid.NewGuid().ToString(),
            RequesterId = "DispatchingAgent"
        };

        var submodel = CapabilityDescriptionSubmodel.CreateWithIdentifier(Guid.NewGuid().ToString());
        submodel.AddCapabilityContainer(BuildCapabilityDefinition(capabilityName, offeredProperty));

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "Module_A",
            AgentRole = "PlanningAgent"
        };

        context.Set("Planning.CapabilityRequest", requestContext);
        context.Set("CapabilityDescriptionSubmodel", submodel);
        context.Set("CapabilityMatchmaking.EmbeddingProvider", new KeywordEmbeddingProvider());
        context.Set("RequiredCapability", capabilityName);

        return context;
    }

    private static CapabilityMatchmakingNode CreateNode(BTContext context)
    {
        return new CapabilityMatchmakingNode
        {
            Context = context,
            SimilarityThreshold = 0.8
        };
    }

    private static CapabilityContainerDefinition BuildCapabilityDefinition(
        string capabilityName,
        CapabilityPropertyContainerDefinition property)
    {
        return new CapabilityContainerDefinition(
            $"{capabilityName}Container",
            new CapabilityElementDefinition(capabilityName),
            PropertySet: new CapabilityPropertySetDefinition("PropertySet", new[] { property }));
    }

    private static PropertyValueContainerDefinition BuildValueContainer(
        string containerId,
        string propertyId,
        string value,
        string valueType)
    {
        return new PropertyValueContainerDefinition(
            containerId,
            propertyId,
            value,
            valueType);
    }

    private sealed class KeywordEmbeddingProvider : ITextEmbeddingProvider
    {
        public Task<double[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult<double[]?>(Array.Empty<double>());
            }

            var vector = text.Contains("torque", StringComparison.OrdinalIgnoreCase)
                ? new[] { 1d, 0d, 0d }
                : text.Contains("speed", StringComparison.OrdinalIgnoreCase)
                    ? new[] { 0d, 1d, 0d }
                    : new[] { 0d, 0d, 1d };

            return Task.FromResult<double[]?>(vector);
        }
    }
}

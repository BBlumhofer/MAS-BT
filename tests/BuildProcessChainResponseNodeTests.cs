using System.Linq;
using System.Threading.Tasks;
using AasSharpClient.Models.ProcessChain;
using BaSyx.Models.AdminShell;
using MAS_BT.Core;
using MAS_BT.Nodes.Dispatching.ProcessChain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ProcessChainModel = AasSharpClient.Models.ProcessChain.ProcessChain;

namespace MAS_BT.Tests;

public class BuildProcessChainResponseNodeTests
{
    [Fact]
    public async Task Execute_EmbedsOffersDirectlyInProcessChain()
    {
        var context = new BTContext(NullLogger<BTContext>.Instance);
        var negotiation = new ProcessChainNegotiationContext
        {
            ConversationId = "conversation",
            RequesterId = "https://smartfactory.de/shells/LG3JsASu4_"
        };

        var requirement = new CapabilityRequirement
        {
            Capability = "Assemble",
            RequirementId = "req-1"
        };

        var offer = new OfferedCapability("Offer_Assemble");
        // attach a model reference like returned from Neo4j (Submodel -> CapabilitySet -> AssembleContainer -> Assemble)
        var keys = new System.Collections.Generic.List<IKey>
        {
            new Key(KeyType.Submodel, "https://smartfactory.de/submodels/capability/3b3c7ec0-ddd0-422b-99c3-7e61aa670f83"),
            new Key(KeyType.SubmodelElementCollection, "CapabilitySet"),
            new Key(KeyType.SubmodelElementCollection, "AssembleContainer"),
            new Key(KeyType.Capability, "Assemble")
        };
        var modelRef = new Reference(keys) { Type = ReferenceType.ModelReference };
        offer.OfferedCapabilityReference.Value = new ReferenceElementValue(modelRef);
        requirement.CapabilityOffers.Add(offer);
        negotiation.Requirements.Add(requirement);

        // provide a fake capability reference query that returns the same model reference as JSON (as Neo4j would)
        context.Set("CapabilityReferenceQuery", new FakeCapabilityReferenceQuery());
        context.Set("ProcessChain.Negotiation", negotiation);

        var node = new BuildProcessChainResponseNode { Context = context };
        var status = await node.Execute();

        Assert.Equal(NodeStatus.Success, status);
        var processChain = context.Get<ProcessChainModel>("ProcessChain.Result");
        Assert.NotNull(processChain);

        var requiredCapability = processChain!.GetRequiredCapabilities().Single();
        var embeddedOffers = requiredCapability.GetOfferedCapabilities().ToList();
        Assert.Single(embeddedOffers);
        Assert.Same(offer, embeddedOffers.Single());
    }

    private sealed class FakeCapabilityReferenceQuery : MAS_BT.Services.Graph.ICapabilityReferenceQuery
    {
        public System.Threading.Tasks.Task<string?> GetCapabilityReferenceJsonAsync(string moduleShellId, string capabilityIdShort, System.Threading.CancellationToken cancellationToken = default)
        {
            var json = "[{" +
                       "\"type\": \"Submodel\", \"value\": \"https://smartfactory.de/submodels/capability/3b3c7ec0-ddd0-422b-99c3-7e61aa670f83\"" +
                       "}, {\"type\": \"SubmodelElementCollection\", \"value\": \"CapabilitySet\"}, {\"type\": \"SubmodelElementCollection\", \"value\": \"AssembleContainer\"}, {\"type\": \"Capability\", \"value\": \"Assemble\"}]";
            return System.Threading.Tasks.Task.FromResult<string?>(json);
        }
    }
}

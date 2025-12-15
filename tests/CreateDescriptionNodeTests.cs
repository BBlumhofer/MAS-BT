using System.Collections.Concurrent;
using System.Threading.Tasks;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Core;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using Xunit;

namespace MAS_BT.Tests;

public class CreateDescriptionNodeTests
{
    [Fact]
    public async Task Execute_UsesCachedDescription_ForRepeatedCapability()
    {
        var ctx = new BTContext();

        var cache = new ConcurrentDictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Assemble"] = "Meaning: cached description text"
        };
        ctx.Set("Similarity.DescriptionCache", cache);

        var msg = new I40MessageBuilder()
            .From("DispatchingAgent_phuket", "DispatchingAgent")
            .To("SimilarityAnalysisAgent_phuket", "AIAgent")
            .WithType("createDescription")
            .WithConversationId("conv-1")
            .AddElement(new Property<string>("Capability_0") { Value = new PropertyValue<string>("Assemble") })
            .Build();

        ctx.Set("CurrentMessage", msg);

        var node = new CreateDescriptionNode
        {
            Context = ctx,
            // If the cache is not used, this would try to call a non-existing endpoint and fail.
            OllamaEndpoint = "http://127.0.0.1:1",
            Model = "llama3"
        };

        var status = await node.Execute();

        Assert.Equal(NodeStatus.Success, status);
        Assert.Equal("Meaning: cached description text", ctx.Get<string>("Description_Result"));
        Assert.Same(msg, ctx.Get<I40Sharp.Messaging.Models.I40Message>("CreateDescriptionTargetMessage"));
    }
}

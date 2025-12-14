using BaSyx.Models.AdminShell;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Nodes.Planning.ProcessChain;
using Xunit;

namespace MAS_BT.Tests;

public class CapabilityRequestContextTests
{
    [Fact]
    public void FromMessage_AttachesCapabilityContainer()
    {
        var capabilityContainer = new SubmodelElementCollection("AssembleContainer");
        capabilityContainer.Add(new Capability("Assemble"));

        var message = new I40MessageBuilder()
            .From("DispatchingAgent", "DispatchingAgent")
            .To("Module101", "ModuleHolon")
            .WithType(I40MessageTypes.CALL_FOR_PROPOSAL)
            .WithConversationId("conv-test")
            .AddElement(new Property<string>("Capability") { Value = new PropertyValue<string>("Assemble") })
            .AddElement(new Property<string>("RequirementId") { Value = new PropertyValue<string>("req-777") })
            .AddElement(capabilityContainer)
            .Build();

        var context = CapabilityRequestContext.FromMessage(message);

        Assert.Equal("Assemble", context.Capability);
        Assert.Equal("req-777", context.RequirementId);
        Assert.NotNull(context.CapabilityContainer);
        Assert.Equal("AssembleContainer", context.CapabilityContainer!.IdShort);
        Assert.Equal("Assemble", context.CapabilityContainer.Capability?.IdShort);
    }
}

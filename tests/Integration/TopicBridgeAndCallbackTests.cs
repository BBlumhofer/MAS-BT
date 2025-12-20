using System;
using System.Linq;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using MAS_BT.Services.TopicBridge;
using MAS_BT.Tests.TestHelpers;
using Xunit;

namespace MAS_BT.Tests.Integration;

public class TopicBridgeAndCallbackTests
{
    [Fact]
    public async Task TopicBridge_Forwards_Messages_From_Source_To_Target()
    {
        Environment.SetEnvironmentVariable("MASBT_TEST_USE_REAL_MQTT", "true");
        Environment.SetEnvironmentVariable("MASBT_TEST_MQTT_HOST", "localhost");
        Environment.SetEnvironmentVariable("MASBT_TEST_MQTT_PORT", "1883");

        var source = "/_TB_TEST/source";
        var target = "/_TB_TEST/target";

        // Bridge transport + client (will be used for subscription)
        var bridgeTransport = TestTransportFactory.CreateTransport("bridge");
        var bridgeClient = new MessagingClient(bridgeTransport, "/_bridge_default");
        await bridgeClient.ConnectAsync();

        var bridgeService = new TopicBridgeService(bridgeClient, bridgeTransport);
        bridgeService.AddRule(source, target);
        await bridgeService.InitializeAsync();

        // Target listener
        var targetTransport = TestTransportFactory.CreateTransport("target");
        var targetClient = new MessagingClient(targetTransport, target);
        await targetClient.ConnectAsync();

        // Publisher
        var pubTransport = TestTransportFactory.CreateTransport("pub");
        var pubClient = new MessagingClient(pubTransport, "/_pub_default");
        await pubClient.ConnectAsync();

        try
        {
            var msg = new I40Sharp.Messaging.Core.I40MessageBuilder()
                .From("pub", "role")
                .To("any", "role")
                .WithType("forwardTest")
                .Build();

            await pubClient.PublishAsync(msg, source);

            await Task.Delay(400);

            var found = targetClient.DequeueMatchingAll((m, t) => (m.Frame?.Type ?? string.Empty).Contains("forwardTest"));
            Assert.True(found.Count > 0, "Target should have received forwarded message");
        }
        finally
        {
            bridgeClient.Dispose();
            targetClient.Dispose();
            pubClient.Dispose();
        }
    }

    [Fact]
    public void CallbackRegistry_Matches_MessageType_And_Sender()
    {
        var registry = new I40Sharp.Messaging.Core.CallbackRegistry();

        var calledType = false;
        var calledSender = false;

        registry.RegisterMessageTypeCallback("testType", m => calledType = true);
        registry.RegisterSenderCallback("senderX", m => calledSender = true);

        var msg = new I40Sharp.Messaging.Models.I40Message();
        msg.Frame.Type = "testType";
        msg.Frame.Sender = new I40Sharp.Messaging.Models.Participant { Identification = new I40Sharp.Messaging.Models.Identification { Id = "senderX" } };

        registry.InvokeCallbacks(msg);

        Assert.True(calledType, "MessageType callback should be invoked");
        Assert.True(calledSender, "Sender callback should be invoked");
    }
}

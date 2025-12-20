using System;
using System.Linq;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using MAS_BT.Tests.TestHelpers;
using Xunit;

namespace MAS_BT.Tests.Integration;

public class NamespaceHolonConfigTests
{
    [Fact]
    public async Task TestTransportFactory_UsesRealMqttAndEndToEndPublishReceive()
    {
        // Arrange: force real MQTT usage and localhost broker
        Environment.SetEnvironmentVariable("MASBT_TEST_USE_REAL_MQTT", "true");
        Environment.SetEnvironmentVariable("MASBT_TEST_MQTT_HOST", "localhost");
        Environment.SetEnvironmentVariable("MASBT_TEST_MQTT_PORT", "1883");

        var topic = "/_SANITY_CONFIG_TEST/register";

        // Act: create listener client
        var listener = await TestTransportFactory.CreateClientAsync(topic, "listener");
        try
        {
            Assert.True(listener.IsConnected, "Listener should be connected to broker");

            // Create a publisher client
            var publisher = await TestTransportFactory.CreateClientAsync(topic, "publisher");
            try
            {
                Assert.True(publisher.IsConnected, "Publisher should be connected to broker");

                // Build a minimal register message
                var msg = new I40MessageBuilder()
                    .From("pub", "role")
                    .To("any", "role")
                    .WithType("registerMessage")
                    .Build();

                await publisher.PublishAsync(msg);

                // Allow some time for delivery
                await Task.Delay(300);

                // Assert: listener buffered the message
                var found = listener.DequeueMatchingAll((m, t) => (m.Frame?.Type ?? string.Empty).Contains("registerMessage"));
                Assert.True(found.Count > 0, "Listener should have received at least one registerMessage");
            }
            finally
            {
                publisher.Dispose();
            }
        }
        finally
        {
            listener.Dispose();
        }
    }
}

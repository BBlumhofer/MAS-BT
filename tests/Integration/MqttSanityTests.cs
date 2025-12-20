using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using MAS_BT.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests.Integration
{
    public class MqttSanityTests
    {
        private const string Host = "localhost";
        private const int Port = 1883;

        [Fact]
        public async Task Broker_IsReachable()
        {
            var transport = new MqttTransport(Host, Port, $"test-client-{Guid.NewGuid():N}");
            var client = new MessagingClient(transport, "test/default");

            await client.ConnectAsync();
            Assert.True(client.IsConnected, "Client should be connected to local MQTT broker");

            await client.DisconnectAsync();
        }

        [Fact]
        public async Task WildcardSubscription_Receives_Namespace_and_ModuleTopics()
        {
            var ns = $"_SANITY_{Guid.NewGuid():N}";
            var subscriber = new MessagingClient(new MqttTransport(Host, Port, $"sub-{Guid.NewGuid():N}"), "test/default");
            var publisher = new MessagingClient(new MqttTransport(Host, Port, $"pub-{Guid.NewGuid():N}"), "test/default");

            var received = new List<I40Message>();

            await subscriber.ConnectAsync();
            await publisher.ConnectAsync();

            // Subscribe to namespace wildcard
            await subscriber.SubscribeAsync($"/{ns}/#");
            subscriber.OnMessage(m => received.Add(m));

            await Task.Delay(200);

            // publish namespace-level register
            var msg1 = new I40MessageBuilder().From("A","Role").To("Namespace",null).WithType("registerMessage").Build();
            await publisher.PublishAsync(msg1, $"/{ns}/register");

            // publish module-level register
            var msg2 = new I40MessageBuilder().From("B","Role").To("Namespace",null).WithType("registerMessage").Build();
            await publisher.PublishAsync(msg2, $"/{ns}/SomeModule/register");

            // allow receipts
            await Task.Delay(500);

            Assert.True(received.Any(r => r.Frame?.Type == "registerMessage"), "At least one registerMessage should be received");
            // Expect at least two (namespace and module)
            Assert.True(received.Count >= 2, $"Expected >=2 messages, got {received.Count}");

            await subscriber.DisconnectAsync();
            await publisher.DisconnectAsync();
        }

        [Fact]
        public async Task DequeueMatchingAll_Returns_BufferedMessages()
        {
            var ns = $"_SANITY_{Guid.NewGuid():N}";
            var transport = new MqttTransport(Host, Port, $"buftest-{Guid.NewGuid():N}");
            var client = new MessagingClient(transport, $"{ns}/logs");

            await client.ConnectAsync();
            // subscribe to namespace so broker will route
            await client.SubscribeAsync($"/{ns}/#");

            // publish a few messages using a separate publisher
            var pub = new MessagingClient(new MqttTransport(Host, Port, $"pub-{Guid.NewGuid():N}"), "unused");
            await pub.ConnectAsync();
            var m1 = new I40MessageBuilder().From("X","Role").To("Namespace",null).WithType("testType").Build();
            await pub.PublishAsync(m1, $"/{ns}/a");
            var m2 = new I40MessageBuilder().From("Y","Role").To("Namespace",null).WithType("other").Build();
            await pub.PublishAsync(m2, $"/{ns}/b");

            await Task.Delay(300);

            var matches = client.DequeueMatchingAll((msg, topic) => msg.Frame?.Type == "testType");
            Assert.Contains(matches, t => t.Message.Frame?.Type == "testType");

            await pub.DisconnectAsync();
            await client.DisconnectAsync();
        }
    }
}

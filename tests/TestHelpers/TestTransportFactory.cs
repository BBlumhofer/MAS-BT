using System;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;

namespace MAS_BT.Tests.TestHelpers;

public static class TestTransportFactory
{
    private static readonly bool UseRealMqtt = !string.Equals(Environment.GetEnvironmentVariable("MASBT_TEST_USE_REAL_MQTT") ?? "true", "false", StringComparison.OrdinalIgnoreCase);
    private static readonly string RealMqttHost = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_BROKER")
        ?? Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_HOST")
        ?? "localhost";
    private static readonly int RealMqttPort = int.TryParse(Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PORT") ?? "1883", out var p) ? p : 1883;
    private static readonly string? RealMqttUser = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_USERNAME");
    private static readonly string? RealMqttPass = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PASSWORD");

    public static IMessagingTransport CreateTransport(string clientIdPrefix = "test-client")
    {
        if (UseRealMqtt)
        {
            var id = $"{clientIdPrefix}-{Guid.NewGuid():N}";
            return new MqttTransport(RealMqttHost, RealMqttPort, id, RealMqttUser, RealMqttPass);
        }

        return new TestHelpers.InMemoryTransport();
    }

    public static async Task<MessagingClient> CreateClientAsync(string defaultTopic, string clientIdPrefix = "test-client")
    {
        var transport = CreateTransport(clientIdPrefix);
        var client = new MessagingClient(transport, defaultTopic);
        await client.ConnectAsync();
        return client;
    }
}

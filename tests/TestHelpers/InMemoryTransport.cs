using System.Collections.Concurrent;
using System.Linq;
using I40Sharp.Messaging.Transport;

namespace MAS_BT.Tests.TestHelpers;

/// <summary>
/// Simple in-memory transport for tests. Publishes to all subscribers of a topic within the same process.
/// </summary>
public class InMemoryTransport : IMessagingTransport
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<InMemoryTransport>> ExactSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentBag<InMemoryTransport>> PatternSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _connected;

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public bool IsConnected => _connected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        Connected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        if (!_connected)
            throw new InvalidOperationException("Transport not connected");

        if (ExactSubscriptions.TryGetValue(topic, out var subscribers))
        {
            foreach (var subscriber in subscribers)
            {
                subscriber.MessageReceived?.Invoke(this, new MessageReceivedEventArgs
                {
                    Topic = topic,
                    Payload = payload
                });
            }
        }

        // Wildcard subscriptions (+, #)
        foreach (var kv in PatternSubscriptions)
        {
            if (!TopicMatches(kv.Key, topic))
            {
                continue;
            }

            foreach (var subscriber in kv.Value)
            {
                subscriber.MessageReceived?.Invoke(this, new MessageReceivedEventArgs
                {
                    Topic = topic,
                    Payload = payload
                });
            }
        }

        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        var dict = (topic.Contains('+') || topic.Contains('#')) ? PatternSubscriptions : ExactSubscriptions;
        var bag = dict.GetOrAdd(topic, _ => new ConcurrentBag<InMemoryTransport>());
        bag.Add(this);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        var dict = (topic.Contains('+') || topic.Contains('#')) ? PatternSubscriptions : ExactSubscriptions;
        if (dict.TryGetValue(topic, out var bag))
        {
            // rebuild bag without this transport
            var remaining = new ConcurrentBag<InMemoryTransport>(bag.Where(t => t != this));
            dict[topic] = remaining;
        }
        return Task.CompletedTask;
    }

    private static bool TopicMatches(string pattern, string topic)
    {
        if (string.Equals(pattern, topic, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // MQTT matching rules (simplified):
        // '+' matches a single level, '#' matches all remaining levels (only valid as last token).
        var pLevels = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tLevels = topic.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var pIndex = 0;
        var tIndex = 0;
        while (pIndex < pLevels.Length && tIndex < tLevels.Length)
        {
            var p = pLevels[pIndex];
            if (p == "#")
            {
                return pIndex == pLevels.Length - 1;
            }

            if (p != "+" && !string.Equals(p, tLevels[tIndex], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            pIndex++;
            tIndex++;
        }

        if (pIndex == pLevels.Length && tIndex == tLevels.Length)
        {
            return true;
        }

        // pattern has trailing '#'
        if (pIndex == pLevels.Length - 1 && pLevels[pIndex] == "#")
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        // nothing to clean up beyond unsubscribing
    }
}

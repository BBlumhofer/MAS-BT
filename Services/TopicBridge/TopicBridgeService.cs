using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Transport;
using System.IO.Hashing;

namespace MAS_BT.Services.TopicBridge;

/// <summary>
/// Routes raw MQTT payloads between external and internal topics so sub-holons can
/// communicate exclusively with their parent while the parent mirrors messages to the
/// global namespace.
/// </summary>
public sealed class TopicBridgeService
{
    private readonly IMessagingTransport _transport;
    private readonly MessagingClient _client;
    private readonly List<TopicBridgeRule> _rules = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentlyForwarded = new();
    private readonly TimeSpan _suppressWindow = TimeSpan.FromSeconds(1);
    private bool _initialized;

    public TopicBridgeService(MessagingClient client, IMessagingTransport transport)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public void AddRule(string sourcePattern, params string[] targetTemplates)
    {
        if (string.IsNullOrWhiteSpace(sourcePattern) || targetTemplates == null || targetTemplates.Length == 0)
        {
            return;
        }

        _rules.Add(new TopicBridgeRule(sourcePattern, targetTemplates));
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        // Subscribe to every unique pattern (wildcards allowed).
        var filters = _rules
            .Select(r => r.SourcePattern.RawPattern)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var filter in filters)
        {
            try
            {
                await _client.SubscribeAsync(filter).ConfigureAwait(false);
            }
            catch
            {
                // Keep going even if a filter fails (topic may already be covered).
            }
        }

        _transport.MessageReceived += OnMessageReceived;
        _initialized = true;
    }

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        var topic = e.Topic ?? string.Empty;
        var payload = e.Payload ?? string.Empty;

        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        _ = Task.Run(() => HandleMessageAsync(topic, payload));
    }

    private async Task HandleMessageAsync(string topic, string payload)
    {
        if (ShouldSuppress(topic, payload))
        {
            return;
        }

        foreach (var rule in _rules)
        {
            if (!rule.TryMap(topic, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                try
                {
                    MarkForwarded(target, payload);
                    await _transport.PublishAsync(target, payload).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore transient publish failures; caller will retry on next message.
                }
            }

            break;
        }
    }

    private bool ShouldSuppress(string topic, string payload)
    {
        var key = BuildHashKey(topic, payload);
        if (_recentlyForwarded.TryGetValue(key, out var timestamp))
        {
            if (DateTime.UtcNow - timestamp <= _suppressWindow)
            {
                _recentlyForwarded.TryRemove(key, out _);
                return true;
            }

            _recentlyForwarded.TryRemove(key, out _);
        }

        return false;
    }

    private void MarkForwarded(string topic, string payload)
    {
        var key = BuildHashKey(topic, payload);
        _recentlyForwarded[key] = DateTime.UtcNow;
    }

    private static string BuildHashKey(string topic, string payload)
    {
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        return $"{topic}|{hash}";
    }
}

internal sealed class TopicBridgeRule
{
    public TopicPattern SourcePattern { get; }
    private readonly string[] _targetTemplates;

    public TopicBridgeRule(string sourcePattern, string[] targetTemplates)
    {
        SourcePattern = new TopicPattern(sourcePattern);
        _targetTemplates = targetTemplates ?? Array.Empty<string>();
    }

    public bool TryMap(string topic, out List<string> targets)
    {
        targets = new List<string>();
        if (!SourcePattern.TryMatch(topic, out var captures))
        {
            return false;
        }

        foreach (var template in _targetTemplates)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            targets.Add(SourcePattern.Format(template, captures));
        }

        return targets.Count > 0;
    }
}

internal sealed class TopicPattern
{
    private readonly string[] _segments;

    public TopicPattern(string pattern)
    {
        RawPattern = NormalizePattern(pattern);
        _segments = RawPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public string RawPattern { get; }

    public bool TryMatch(string topic, out List<string> captures)
    {
        captures = new List<string>();
        var topicSegments = NormalizePattern(topic).Split('/', StringSplitOptions.RemoveEmptyEntries);

        var ti = 0;
        for (var pi = 0; pi < _segments.Length; pi++)
        {
            var token = _segments[pi];
            if (token == "#")
            {
                var remaining = string.Join('/', topicSegments.Skip(ti));
                captures.Add(remaining);
                return true;
            }

            if (ti >= topicSegments.Length)
            {
                return false;
            }

            if (token == "+")
            {
                captures.Add(topicSegments[ti]);
                ti++;
                continue;
            }

            if (!string.Equals(token, topicSegments[ti], StringComparison.OrdinalIgnoreCase))
            {
                captures.Clear();
                return false;
            }

            ti++;
        }

        return ti == topicSegments.Length;
    }

    public string Format(string template, IReadOnlyList<string> captures)
    {
        if (string.IsNullOrWhiteSpace(template) || captures.Count == 0 || !template.Contains('{'))
        {
            return NormalizePattern(template);
        }

        try
        {
            return NormalizePattern(string.Format(template, captures.ToArray()));
        }
        catch
        {
            return NormalizePattern(template);
        }
    }

    private static string NormalizePattern(string topic)
    {
        var normalized = topic?.Trim() ?? string.Empty;
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.TrimEnd('/');
    }
}

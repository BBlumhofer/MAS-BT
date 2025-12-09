using System;
using System.Collections.Generic;
using System.Linq;
using ActionModel = AasSharpClient.Models.Action;

namespace MAS_BT.Services;

/// <summary>
/// Immutable envelope that keeps the complete I4.0 SkillRequest + parsed metadata.
/// Conversation/Product ID as well as sender/receiver stay attached to the request for
/// the entire lifecycle.
/// </summary>
public class SkillRequestEnvelope
{
    public SkillRequestEnvelope(
        string rawMessage,
        string conversationId,
        string senderId,
        string receiverId,
        string actionId,
        string actionTitle,
        string machineName,
        string actionStatus,
        IDictionary<string, object> inputParameters,
        ActionModel actionModel)
    {
        RawMessage = rawMessage;
        ConversationId = conversationId;
        ProductId = conversationId;
        SenderId = senderId;
        ReceiverId = receiverId;
        ActionId = actionId;
        ActionTitle = actionTitle;
        MachineName = machineName;
        ActionStatus = actionStatus;
        InputParameters = new Dictionary<string, object>(inputParameters, StringComparer.OrdinalIgnoreCase);
        ActionModel = actionModel;
        EnqueuedAt = DateTime.UtcNow;
        QueueState = SkillRequestQueueState.Pending;
    }

    public string RawMessage { get; }
    public string ConversationId { get; }
    public string ProductId { get; }
    public string SenderId { get; }
    public string ReceiverId { get; }
    public string ActionId { get; }
    public string ActionTitle { get; }
    public string MachineName { get; }
    public string ActionStatus { get; }
    public IReadOnlyDictionary<string, object> InputParameters { get; }
    public ActionModel ActionModel { get; }
    public DateTime EnqueuedAt { get; }
    public int RetryAttempts { get; private set; }
    public DateTime? NextRetryUtc { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public SkillRequestQueueState QueueState { get; private set; }

    internal void MarkRunning()
    {
        QueueState = SkillRequestQueueState.Running;
        StartedAt ??= DateTime.UtcNow;
    }

    internal void MarkPending()
    {
        QueueState = SkillRequestQueueState.Pending;
    }

    internal void IncrementRetry(TimeSpan backoff)
    {
        RetryAttempts++;
        NextRetryUtc = DateTime.UtcNow.Add(backoff);
    }

    internal void ResetRetry()
    {
        RetryAttempts = 0;
        NextRetryUtc = null;
    }

    public override string ToString()
    {
        return $"{ActionTitle} ({ConversationId})";
    }
}

public enum SkillRequestQueueState
{
    Pending,
    Running
}

/// <summary>
/// Thread-safe execution queue for SkillRequests. Stores the entire envelope so
/// follow-up nodes can always recover context (conversation/product ID, sender, etc.).
/// Supports marking entries as running and removing them once finished/aborted.
/// </summary>
public class SkillRequestQueue
{
    private readonly List<SkillRequestEnvelope> _items = new();
    private readonly object _sync = new();

    public SkillRequestQueue(int capacity = 0)
    {
        Capacity = capacity < 0 ? 0 : capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public bool TryEnqueue(SkillRequestEnvelope envelope, out int queueLength)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_sync)
        {
            var currentCount = _items.Count;
            if (Capacity > 0 && currentCount >= Capacity)
            {
                queueLength = currentCount;
                return false;
            }

            _items.Add(envelope);
            queueLength = _items.Count;
            return true;
        }
    }

    /// <summary>
    /// Marks the next pending entry as running without removing it from the queue.
    /// Returns false if no pending entries are available.
    /// </summary>
    public bool TryStartNext(out SkillRequestEnvelope? envelope)
    {
        lock (_sync)
        {
            // Find first pending item whose NextRetryUtc is not in the future
            var now = DateTime.UtcNow;
            envelope = _items.FirstOrDefault(item => item.QueueState == SkillRequestQueueState.Pending && (item.NextRetryUtc == null || item.NextRetryUtc <= now));
            if (envelope == null)
            {
                return false;
            }

            envelope.MarkRunning();
            return true;
        }
    }

    public bool TryRemoveByConversationId(string? conversationId, out SkillRequestEnvelope? removed)
    {
        removed = null;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        lock (_sync)
        {
            var index = _items.FindIndex(item => string.Equals(item.ConversationId, conversationId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            removed = _items[index];
            _items.RemoveAt(index);
            return true;
        }
    }

    public bool TryRequeueByConversationId(string? conversationId, out SkillRequestEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        lock (_sync)
        {
            // Requeue by moving the item to the end of the list and mark pending
            var index = _items.FindIndex(item => string.Equals(item.ConversationId, conversationId, StringComparison.Ordinal));
            if (index < 0) return false;

            var item = _items[index];
            _items.RemoveAt(index);
            item.MarkPending();
            _items.Add(item);
            envelope = item;
            return true;
        }
    }

    /// <summary>
    /// Move the matching envelope to the end of the queue and mark it pending.
    /// Returns false if not found.
    /// </summary>
    public bool MoveToEndByConversationId(string? conversationId, out SkillRequestEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(conversationId)) return false;

        lock (_sync)
        {
            var index = _items.FindIndex(item => string.Equals(item.ConversationId, conversationId, StringComparison.Ordinal));
            if (index < 0) return false;

            var item = _items[index];
            _items.RemoveAt(index);
            item.MarkPending();
            _items.Add(item);
            envelope = item;
            return true;
        }
    }

    public IReadOnlyCollection<SkillRequestEnvelope> Snapshot()
    {
        lock (_sync)
        {
            return _items.Select(item => item).ToArray();
        }
    }
}

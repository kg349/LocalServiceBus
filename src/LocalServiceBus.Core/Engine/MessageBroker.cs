using System.Collections.Concurrent;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Engine;

public sealed class MessageBroker : IDisposable
{
    private readonly ConcurrentDictionary<string, QueueEngine> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TopicEngine> _topics = new(StringComparer.OrdinalIgnoreCase);

    public QueueEngine GetOrCreateQueue(string queueName, int maxDeliveryCount = 10, TimeSpan? lockDuration = null)
    {
        return _queues.GetOrAdd(queueName, name => new QueueEngine(new QueueEntity
        {
            Name = name,
            MaxDeliveryCount = maxDeliveryCount,
            LockDuration = lockDuration ?? TimeSpan.FromSeconds(30)
        }));
    }

    public TopicEngine GetOrCreateTopic(string topicName)
    {
        return _topics.GetOrAdd(topicName, name => new TopicEngine(new TopicEntity { Name = name }));
    }

    public QueueEngine? GetQueue(string queueName)
    {
        return _queues.TryGetValue(queueName, out var queue) ? queue : null;
    }

    public TopicEngine? GetTopic(string topicName)
    {
        return _topics.TryGetValue(topicName, out var topic) ? topic : null;
    }

    public QueueEngine GetOrCreateSubscription(string topicName, string subscriptionName,
        int maxDeliveryCount = 10, TimeSpan? lockDuration = null)
    {
        var topic = GetOrCreateTopic(topicName);
        return topic.GetOrAddSubscription(subscriptionName, maxDeliveryCount, lockDuration);
    }

    public async Task SendToQueueAsync(string queueName, BrokerMessage message)
    {
        var queue = GetOrCreateQueue(queueName);
        await queue.EnqueueAsync(message);
    }

    public async Task SendToTopicAsync(string topicName, BrokerMessage message)
    {
        var topic = GetOrCreateTopic(topicName);
        await topic.PublishAsync(message);
    }

    public async Task<BrokerMessage?> ReceiveFromQueueAsync(string queueName,
        TimeSpan? lockDuration = null, CancellationToken ct = default)
    {
        var queue = GetOrCreateQueue(queueName);
        return await queue.DequeueAsync(lockDuration, ct);
    }

    public async Task<BrokerMessage?> ReceiveFromSubscriptionAsync(string topicName,
        string subscriptionName, TimeSpan? lockDuration = null, CancellationToken ct = default)
    {
        var sub = GetOrCreateSubscription(topicName, subscriptionName);
        return await sub.DequeueAsync(lockDuration, ct);
    }

    public async Task CompleteAsync(string entityPath, Guid lockToken)
    {
        var queue = ResolveQueue(entityPath);
        await queue.CompleteAsync(lockToken);
    }

    public async Task DeadLetterAsync(string entityPath, Guid lockToken,
        string? reason = null, string? errorDescription = null)
    {
        var queue = ResolveQueue(entityPath);
        await queue.DeadLetterAsync(lockToken, reason, errorDescription);
    }

    public async Task AbandonAsync(string entityPath, Guid lockToken)
    {
        var queue = ResolveQueue(entityPath);
        await queue.AbandonAsync(lockToken);
    }

    public DateTimeOffset RenewLock(Guid lockToken)
    {
        foreach (var queue in _queues.Values)
        {
            try { return queue.RenewLock(lockToken); } catch { }
        }

        foreach (var topic in _topics.Values)
        {
            foreach (var subName in topic.GetSubscriptionNames())
            {
                var sub = topic.GetSubscription(subName);
                if (sub is null) continue;
                try { return sub.RenewLock(lockToken); } catch { }
            }
        }

        throw new InvalidOperationException($"Lock token {lockToken} not found in any entity.");
    }

    public IReadOnlyList<string> GetQueueNames() => _queues.Keys.ToList();
    public IReadOnlyList<string> GetTopicNames() => _topics.Keys.ToList();

    public BrokerStats GetStats()
    {
        var queueStats = _queues.Select(q => new QueueStats(
            q.Key, q.Value.ActiveMessageCount, q.Value.DeadLetterCount)).ToList();

        var topicStats = _topics.Select(t => new TopicStats(
            t.Key,
            t.Value.GetSubscriptionNames().Select(s =>
            {
                var sub = t.Value.GetSubscription(s)!;
                return new QueueStats($"{t.Key}/Subscriptions/{s}", sub.ActiveMessageCount, sub.DeadLetterCount);
            }).ToList()
        )).ToList();

        return new BrokerStats(queueStats, topicStats);
    }

    public void Reset()
    {
        foreach (var q in _queues.Values) q.Dispose();
        foreach (var t in _topics.Values) t.Dispose();
        _queues.Clear();
        _topics.Clear();
    }

    public void Dispose() => Reset();

    private QueueEngine ResolveQueue(string entityPath)
    {
        if (_queues.TryGetValue(entityPath, out var queue))
            return queue;

        var idx = entityPath.IndexOf("/Subscriptions/", StringComparison.OrdinalIgnoreCase);
        var parts = idx >= 0
            ? new[] { entityPath[..idx], entityPath[(idx + "/Subscriptions/".Length)..] }
            : new[] { entityPath };
        if (parts.Length == 2)
        {
            var topic = GetTopic(parts[0]);
            var sub = topic?.GetSubscription(parts[1]);
            if (sub is not null) return sub;
        }

        throw new InvalidOperationException($"Entity '{entityPath}' not found.");
    }
}

public record QueueStats(string Name, int ActiveMessages, int DeadLetterMessages);
public record TopicStats(string Name, List<QueueStats> Subscriptions);
public record BrokerStats(List<QueueStats> Queues, List<TopicStats> Topics);

using System.Collections.Concurrent;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Engine;

public sealed class TopicEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, QueueEngine> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public TopicEntity Entity { get; }

    public TopicEngine(TopicEntity entity)
    {
        Entity = entity;
    }

    public QueueEngine GetOrAddSubscription(string subscriptionName, int maxDeliveryCount = 10, TimeSpan? lockDuration = null)
    {
        return _subscriptions.GetOrAdd(subscriptionName, name => new QueueEngine(new QueueEntity
        {
            Name = $"{Entity.Name}/Subscriptions/{name}",
            MaxDeliveryCount = maxDeliveryCount,
            LockDuration = lockDuration ?? TimeSpan.FromSeconds(30)
        }));
    }

    public QueueEngine? GetSubscription(string subscriptionName)
    {
        return _subscriptions.TryGetValue(subscriptionName, out var queue) ? queue : null;
    }

    public bool RemoveSubscription(string subscriptionName)
    {
        if (!_subscriptions.TryRemove(subscriptionName, out var queue)) return false;
        queue.Dispose();
        return true;
    }

    public IReadOnlyList<string> GetSubscriptionNames() => _subscriptions.Keys.ToList();

    public async Task PublishAsync(BrokerMessage message)
    {
        var tasks = _subscriptions.Values.Select(sub => sub.EnqueueAsync(message.Clone()));
        await Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
            sub.Dispose();
    }
}

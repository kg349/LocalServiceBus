namespace LocalServiceBus.Core.Models;

public class SubscriptionEntity
{
    public required string TopicName { get; init; }
    public required string SubscriptionName { get; init; }
    public int MaxDeliveryCount { get; init; } = 10;
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(30);
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

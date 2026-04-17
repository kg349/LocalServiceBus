namespace LocalServiceBus.Core.Models;

public class QueueEntity
{
    public required string Name { get; init; }
    public int MaxDeliveryCount { get; init; } = 10;
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(30);
    public bool IsDlq { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

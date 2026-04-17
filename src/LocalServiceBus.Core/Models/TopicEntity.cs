namespace LocalServiceBus.Core.Models;

public class TopicEntity
{
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

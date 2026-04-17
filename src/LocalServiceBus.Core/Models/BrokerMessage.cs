namespace LocalServiceBus.Core.Models;

public class BrokerMessage
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public ReadOnlyMemory<byte> Body { get; init; }
    public string? ContentType { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? To { get; init; }
    public string? ReplyTo { get; init; }
    public string? SessionId { get; init; }
    public string? PartitionKey { get; init; }
    public TimeSpan TimeToLive { get; init; } = TimeSpan.MaxValue;
    public Dictionary<string, object> ApplicationProperties { get; init; } = new();
    public int DeliveryCount { get; set; }
    public DateTimeOffset EnqueuedTime { get; init; } = DateTimeOffset.UtcNow;
    public long SequenceNumber { get; set; }
    public Guid LockToken { get; set; }
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }

    public BrokerMessage Clone()
    {
        return new BrokerMessage
        {
            MessageId = MessageId,
            Body = Body.ToArray(),
            ContentType = ContentType,
            CorrelationId = CorrelationId,
            Subject = Subject,
            To = To,
            ReplyTo = ReplyTo,
            SessionId = SessionId,
            PartitionKey = PartitionKey,
            TimeToLive = TimeToLive,
            ApplicationProperties = new Dictionary<string, object>(ApplicationProperties),
            DeliveryCount = DeliveryCount,
            EnqueuedTime = EnqueuedTime,
            SequenceNumber = SequenceNumber
        };
    }
}

using System.Threading.Channels;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Engine;

public sealed class QueueEngine : IDisposable
{
    private readonly Channel<BrokerMessage> _channel;
    private readonly LockManager _lockManager;
    private readonly Lazy<QueueEngine> _deadLetterQueue;
    private long _sequenceNumber;

    public QueueEntity Entity { get; }
    public QueueEngine DeadLetterQueue => _deadLetterQueue.Value;
    public int ActiveMessageCount => _channel.Reader.Count;
    public int DeadLetterCount => _deadLetterQueue.IsValueCreated ? _deadLetterQueue.Value.ActiveMessageCount : 0;

    public QueueEngine(QueueEntity entity)
    {
        Entity = entity;
        _channel = Channel.CreateUnbounded<BrokerMessage>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        _lockManager = new LockManager(OnLockExpired);
        _deadLetterQueue = new Lazy<QueueEngine>(() => new QueueEngine(new QueueEntity
        {
            Name = $"{entity.Name}/$deadletterqueue",
            IsDlq = true,
            MaxDeliveryCount = int.MaxValue
        }));
    }

    public async Task EnqueueAsync(BrokerMessage message)
    {
        message.SequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        await _channel.Writer.WriteAsync(message);
    }

    public async Task<BrokerMessage?> DequeueAsync(TimeSpan? lockDuration = null, CancellationToken ct = default)
    {
        var duration = lockDuration ?? Entity.LockDuration;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var message = await _channel.Reader.ReadAsync(cts.Token);
            message.DeliveryCount++;
            _lockManager.Lock(message, duration);
            return message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Task CompleteAsync(Guid lockToken)
    {
        var message = _lockManager.Complete(lockToken);
        if (message is null)
            throw new InvalidOperationException($"Lock token {lockToken} not found or expired.");
        return Task.CompletedTask;
    }

    public async Task DeadLetterAsync(Guid lockToken, string? reason = null, string? errorDescription = null)
    {
        var message = _lockManager.Complete(lockToken);
        if (message is null)
            throw new InvalidOperationException($"Lock token {lockToken} not found or expired.");

        message.DeadLetterReason = reason;
        message.DeadLetterErrorDescription = errorDescription;
        await DeadLetterQueue.EnqueueAsync(message);
    }

    public async Task AbandonAsync(Guid lockToken)
    {
        var message = _lockManager.Abandon(lockToken);
        if (message is null)
            throw new InvalidOperationException($"Lock token {lockToken} not found or expired.");

        if (message.DeliveryCount >= Entity.MaxDeliveryCount)
        {
            message.DeadLetterReason = "MaxDeliveryCountExceeded";
            await DeadLetterQueue.EnqueueAsync(message);
            return;
        }

        await _channel.Writer.WriteAsync(message);
    }

    public DateTimeOffset RenewLock(Guid lockToken)
    {
        return _lockManager.RenewLock(lockToken, Entity.LockDuration);
    }

    public async Task<List<BrokerMessage>> PeekAsync(int maxMessages = 10)
    {
        var messages = new List<BrokerMessage>();
        var drained = new List<BrokerMessage>();

        while (messages.Count < maxMessages && _channel.Reader.TryRead(out var msg))
        {
            messages.Add(msg);
            drained.Add(msg);
        }

        foreach (var msg in drained)
            await _channel.Writer.WriteAsync(msg);

        return messages;
    }

    public void Purge()
    {
        while (_channel.Reader.TryRead(out _)) { }
    }

    public async Task ResubmitDeadLettersAsync()
    {
        if (!_deadLetterQueue.IsValueCreated) return;

        var dlq = _deadLetterQueue.Value;
        while (dlq._channel.Reader.TryRead(out var msg))
        {
            msg.DeadLetterReason = null;
            msg.DeadLetterErrorDescription = null;
            msg.DeliveryCount = 0;
            await EnqueueAsync(msg);
        }
    }

    private async void OnLockExpired(BrokerMessage message)
    {
        if (message.DeliveryCount >= Entity.MaxDeliveryCount)
        {
            message.DeadLetterReason = "MaxDeliveryCountExceeded";
            await DeadLetterQueue.EnqueueAsync(message);
            return;
        }

        await _channel.Writer.WriteAsync(message);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        if (_deadLetterQueue.IsValueCreated)
            _deadLetterQueue.Value.Dispose();
    }
}

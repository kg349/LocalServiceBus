using FluentAssertions;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Tests;

public class QueueEngineTests : IDisposable
{
    private readonly QueueEngine _sut;

    public QueueEngineTests()
    {
        _sut = new QueueEngine(new QueueEntity
        {
            Name = "test-queue",
            MaxDeliveryCount = 3,
            LockDuration = TimeSpan.FromSeconds(30)
        });
    }

    [Fact]
    public async Task EnqueueDequeue_ReturnsMessage()
    {
        var msg = CreateMessage("msg-1");
        await _sut.EnqueueAsync(msg);

        var received = await _sut.DequeueAsync();

        received.Should().NotBeNull();
        received!.MessageId.Should().Be("msg-1");
        received.DeliveryCount.Should().Be(1);
    }

    [Fact]
    public async Task Dequeue_EmptyQueue_ReturnsNull()
    {
        var received = await _sut.DequeueAsync(TimeSpan.FromSeconds(1));
        received.Should().BeNull();
    }

    [Fact]
    public async Task FIFO_Ordering()
    {
        await _sut.EnqueueAsync(CreateMessage("first"));
        await _sut.EnqueueAsync(CreateMessage("second"));
        await _sut.EnqueueAsync(CreateMessage("third"));

        var r1 = await _sut.DequeueAsync();
        var r2 = await _sut.DequeueAsync();
        var r3 = await _sut.DequeueAsync();

        r1!.MessageId.Should().Be("first");
        r2!.MessageId.Should().Be("second");
        r3!.MessageId.Should().Be("third");
    }

    [Fact]
    public async Task Complete_RemovesMessagePermanently()
    {
        await _sut.EnqueueAsync(CreateMessage());
        var received = await _sut.DequeueAsync();

        await _sut.CompleteAsync(received!.LockToken);

        _sut.ActiveMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Abandon_RequeuesMessage()
    {
        await _sut.EnqueueAsync(CreateMessage());
        var received = await _sut.DequeueAsync();

        await _sut.AbandonAsync(received!.LockToken);

        var redelivered = await _sut.DequeueAsync();
        redelivered.Should().NotBeNull();
        redelivered!.DeliveryCount.Should().Be(3); // 1 (first dequeue) + 1 (abandon increment) + 1 (second dequeue)
    }

    [Fact]
    public async Task Abandon_ExceedingMaxDelivery_MovesToDlq()
    {
        await _sut.EnqueueAsync(CreateMessage());

        // MaxDeliveryCount is 3. Each dequeue increments DeliveryCount.
        // After 3 dequeue+abandon cycles, delivery count reaches the max.
        // Dequeue #1: count becomes 1, abandon increments to 2, requeues
        // Dequeue #2: count becomes 3 (2+1), abandon sees count >= max, sends to DLQ
        for (int i = 0; i < 2; i++)
        {
            var received = await _sut.DequeueAsync();
            received.Should().NotBeNull($"dequeue iteration {i} should return a message");
            await _sut.AbandonAsync(received!.LockToken);
        }

        await Task.Delay(100); // allow async DLQ enqueue to complete
        _sut.ActiveMessageCount.Should().Be(0);
        _sut.DeadLetterCount.Should().Be(1);
    }

    [Fact]
    public async Task DeadLetter_MovesMessageToDlq()
    {
        await _sut.EnqueueAsync(CreateMessage());
        var received = await _sut.DequeueAsync();

        await _sut.DeadLetterAsync(received!.LockToken, "BadFormat", "Invalid JSON");

        _sut.ActiveMessageCount.Should().Be(0);
        _sut.DeadLetterCount.Should().Be(1);
    }

    [Fact]
    public async Task Purge_ClearsAllMessages()
    {
        await _sut.EnqueueAsync(CreateMessage());
        await _sut.EnqueueAsync(CreateMessage());
        await _sut.EnqueueAsync(CreateMessage());

        _sut.Purge();

        _sut.ActiveMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task ResubmitDeadLetters_MovesBackToMainQueue()
    {
        await _sut.EnqueueAsync(CreateMessage());
        var received = await _sut.DequeueAsync();
        await _sut.DeadLetterAsync(received!.LockToken, "test");

        await _sut.ResubmitDeadLettersAsync();

        _sut.DeadLetterCount.Should().Be(0);
        _sut.ActiveMessageCount.Should().Be(1);
    }

    [Fact]
    public async Task SequenceNumbers_AreIncremental()
    {
        await _sut.EnqueueAsync(CreateMessage());
        await _sut.EnqueueAsync(CreateMessage());

        var r1 = await _sut.DequeueAsync();
        var r2 = await _sut.DequeueAsync();

        r1!.SequenceNumber.Should().Be(1);
        r2!.SequenceNumber.Should().Be(2);
    }

    public void Dispose() => _sut.Dispose();

    private static BrokerMessage CreateMessage(string? id = null) => new()
    {
        MessageId = id ?? Guid.NewGuid().ToString(),
        Body = "test-body"u8.ToArray()
    };
}

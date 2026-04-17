using FluentAssertions;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Tests;

public class MessageBrokerTests : IDisposable
{
    private readonly MessageBroker _sut = new();

    [Fact]
    public async Task SendToQueue_AndReceive()
    {
        await _sut.SendToQueueAsync("orders", CreateMessage("msg-1"));

        var received = await _sut.ReceiveFromQueueAsync("orders");

        received.Should().NotBeNull();
        received!.MessageId.Should().Be("msg-1");
    }

    [Fact]
    public async Task SendToTopic_FansOutToSubscriptions()
    {
        _sut.GetOrCreateSubscription("events", "sub-a");
        _sut.GetOrCreateSubscription("events", "sub-b");

        await _sut.SendToTopicAsync("events", CreateMessage("msg-1"));

        var a = await _sut.ReceiveFromSubscriptionAsync("events", "sub-a");
        var b = await _sut.ReceiveFromSubscriptionAsync("events", "sub-b");

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        a!.MessageId.Should().Be("msg-1");
        b!.MessageId.Should().Be("msg-1");
    }

    [Fact]
    public async Task Complete_RemovesMessage()
    {
        await _sut.SendToQueueAsync("orders", CreateMessage());
        var received = await _sut.ReceiveFromQueueAsync("orders");

        await _sut.CompleteAsync("orders", received!.LockToken);

        var stats = _sut.GetStats();
        stats.Queues.First(q => q.Name == "orders").ActiveMessages.Should().Be(0);
    }

    [Fact]
    public async Task DeadLetter_MovesMessage()
    {
        await _sut.SendToQueueAsync("orders", CreateMessage());
        var received = await _sut.ReceiveFromQueueAsync("orders");

        await _sut.DeadLetterAsync("orders", received!.LockToken, "BadFormat");

        var queue = _sut.GetQueue("orders")!;
        queue.DeadLetterCount.Should().Be(1);
    }

    [Fact]
    public async Task Abandon_RequeuesMessage()
    {
        await _sut.SendToQueueAsync("orders", CreateMessage());
        var received = await _sut.ReceiveFromQueueAsync("orders");

        await _sut.AbandonAsync("orders", received!.LockToken);

        var redelivered = await _sut.ReceiveFromQueueAsync("orders");
        redelivered.Should().NotBeNull();
    }

    [Fact]
    public void GetOrCreateQueue_IsIdempotent()
    {
        var q1 = _sut.GetOrCreateQueue("orders");
        var q2 = _sut.GetOrCreateQueue("orders");
        q1.Should().BeSameAs(q2);
    }

    [Fact]
    public void GetStats_ReturnsAllEntities()
    {
        _sut.GetOrCreateQueue("q1");
        _sut.GetOrCreateQueue("q2");
        _sut.GetOrCreateTopic("t1");

        var stats = _sut.GetStats();

        stats.Queues.Should().HaveCount(2);
        stats.Topics.Should().HaveCount(1);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        _sut.GetOrCreateQueue("q1");
        _sut.GetOrCreateTopic("t1");

        _sut.Reset();

        _sut.GetQueueNames().Should().BeEmpty();
        _sut.GetTopicNames().Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteOnSubscription_Works()
    {
        _sut.GetOrCreateSubscription("events", "handler");
        await _sut.SendToTopicAsync("events", CreateMessage());

        var received = await _sut.ReceiveFromSubscriptionAsync("events", "handler");
        await _sut.CompleteAsync("events/Subscriptions/handler", received!.LockToken);

        var sub = _sut.GetTopic("events")!.GetSubscription("handler")!;
        sub.ActiveMessageCount.Should().Be(0);
    }

    public void Dispose() => _sut.Dispose();

    private static BrokerMessage CreateMessage(string? id = null) => new()
    {
        MessageId = id ?? Guid.NewGuid().ToString(),
        Body = "test-body"u8.ToArray()
    };
}

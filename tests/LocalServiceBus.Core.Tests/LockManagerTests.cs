using FluentAssertions;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Tests;

public class LockManagerTests : IDisposable
{
    private readonly List<BrokerMessage> _expiredMessages = [];
    private readonly LockManager _sut;

    public LockManagerTests()
    {
        _sut = new LockManager(msg => _expiredMessages.Add(msg));
    }

    [Fact]
    public void Lock_ReturnsUniqueToken()
    {
        var msg = CreateMessage();
        var token = _sut.Lock(msg, TimeSpan.FromMinutes(1));
        token.Should().NotBeEmpty();
    }

    [Fact]
    public void Complete_RemovesLock()
    {
        var msg = CreateMessage();
        var token = _sut.Lock(msg, TimeSpan.FromMinutes(1));

        var completed = _sut.Complete(token);

        completed.Should().BeSameAs(msg);
        _sut.ActiveLockCount.Should().Be(0);
    }

    [Fact]
    public void Complete_WithInvalidToken_ReturnsNull()
    {
        _sut.Complete(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Abandon_ReturnsMessageAndIncrementsDeliveryCount()
    {
        var msg = CreateMessage();
        var originalCount = msg.DeliveryCount;
        var token = _sut.Lock(msg, TimeSpan.FromMinutes(1));

        var abandoned = _sut.Abandon(token);

        abandoned.Should().BeSameAs(msg);
        abandoned!.DeliveryCount.Should().Be(originalCount + 1);
        _sut.ActiveLockCount.Should().Be(0);
    }

    [Fact]
    public async Task ExpiredLock_TriggersCallback()
    {
        var msg = CreateMessage();
        _sut.Lock(msg, TimeSpan.FromMilliseconds(50));

        await Task.Delay(TimeSpan.FromSeconds(2));

        _expiredMessages.Should().ContainSingle();
        _expiredMessages[0].Should().BeSameAs(msg);
    }

    public void Dispose() => _sut.Dispose();

    private static BrokerMessage CreateMessage(string? id = null) => new()
    {
        MessageId = id ?? Guid.NewGuid().ToString(),
        Body = "test"u8.ToArray()
    };
}

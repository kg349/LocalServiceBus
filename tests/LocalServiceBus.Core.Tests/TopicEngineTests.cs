using FluentAssertions;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Tests;

public class TopicEngineTests : IDisposable
{
    private readonly TopicEngine _sut;

    public TopicEngineTests()
    {
        _sut = new TopicEngine(new TopicEntity { Name = "test-topic" });
    }

    [Fact]
    public void GetOrAddSubscription_CreatesSubscription()
    {
        var sub = _sut.GetOrAddSubscription("sub-1");

        sub.Should().NotBeNull();
        _sut.GetSubscriptionNames().Should().ContainSingle("sub-1");
    }

    [Fact]
    public void GetOrAddSubscription_ReturnsSameInstanceOnSecondCall()
    {
        var first = _sut.GetOrAddSubscription("sub-1");
        var second = _sut.GetOrAddSubscription("sub-1");

        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task Publish_FansOutToAllSubscriptions()
    {
        var sub1 = _sut.GetOrAddSubscription("sub-1");
        var sub2 = _sut.GetOrAddSubscription("sub-2");
        var sub3 = _sut.GetOrAddSubscription("sub-3");

        await _sut.PublishAsync(CreateMessage("msg-1"));

        sub1.ActiveMessageCount.Should().Be(1);
        sub2.ActiveMessageCount.Should().Be(1);
        sub3.ActiveMessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Publish_ClonesMessages_SoEachSubscriptionGetsOwnCopy()
    {
        var sub1 = _sut.GetOrAddSubscription("sub-1");
        var sub2 = _sut.GetOrAddSubscription("sub-2");

        await _sut.PublishAsync(CreateMessage("msg-1"));

        var r1 = await sub1.DequeueAsync();
        var r2 = await sub2.DequeueAsync();

        r1.Should().NotBeSameAs(r2);
        r1!.MessageId.Should().Be(r2!.MessageId);
    }

    [Fact]
    public async Task Publish_WithNoSubscriptions_DoesNotThrow()
    {
        var act = () => _sut.PublishAsync(CreateMessage());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void RemoveSubscription_RemovesIt()
    {
        _sut.GetOrAddSubscription("sub-1");
        _sut.RemoveSubscription("sub-1").Should().BeTrue();
        _sut.GetSubscriptionNames().Should().BeEmpty();
    }

    public void Dispose() => _sut.Dispose();

    private static BrokerMessage CreateMessage(string? id = null) => new()
    {
        MessageId = id ?? Guid.NewGuid().ToString(),
        Body = "test-body"u8.ToArray()
    };
}

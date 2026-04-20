using System.Text;
using FluentAssertions;
using LocalServiceBus.Amqp;
using LocalServiceBus.Core.Engine;

namespace LocalServiceBus.Integration.Tests;

/// <summary>
/// Integration tests using the real Amqp.Net Lite client against the emulator.
/// These validate that the AMQP protocol layer correctly routes messages
/// through the broker engine.
/// </summary>
public class AmqpIntegrationTests : IAsyncLifetime
{
    private MessageBroker _broker = null!;
    private AmqpListenerHost _host = null!;
    private const int TestPort = 15672;

    public Task InitializeAsync()
    {
        _broker = new MessageBroker();
        _host = new AmqpListenerHost(_broker, TestPort);
        _host.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        _broker.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a client connection to the emulator using explicit ANONYMOUS SASL.
    /// The emulator advertises [MSSBCBS, ANONYMOUS] — without SaslProfile.Anonymous
    /// the client would skip SASL and send plain AMQP (protocol id 0), which the
    /// SASL-enabled server rejects.
    /// </summary>
    private static Task<global::Amqp.Connection> CreateConnectionAsync()
    {
        var address = new global::Amqp.Address("amqp://localhost:" + TestPort);
        var factory = new global::Amqp.ConnectionFactory();
        factory.SASL.Profile = global::Amqp.Sasl.SaslProfile.Anonymous;
        return factory.CreateAsync(address);
    }

    [Fact]
    public async Task SendAndReceive_Queue_ViaAmqp()
    {
        var connection = await CreateConnectionAsync();
        var session = new global::Amqp.Session(connection);

        var sender = new global::Amqp.SenderLink(session, "test-sender", "integration-queue");
        var amqpMessage = new global::Amqp.Message("Hello Integration Test")
        {
            Properties = new global::Amqp.Framing.Properties { MessageId = "int-test-1" }
        };
        await sender.SendAsync(amqpMessage);
        await sender.CloseAsync();

        var receiver = new global::Amqp.ReceiverLink(session, "test-receiver", "integration-queue");
        var received = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
        received.Should().NotBeNull();
        receiver.Accept(received);
        await receiver.CloseAsync();

        await session.CloseAsync();
        await connection.CloseAsync();

        var queue = _broker.GetQueue("integration-queue");
        queue.Should().NotBeNull();
        queue!.ActiveMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task TopicFanOut_ViaAmqp()
    {
        _broker.GetOrCreateSubscription("integration-topic", "sub-a");
        _broker.GetOrCreateSubscription("integration-topic", "sub-b");

        var connection = await CreateConnectionAsync();
        var session = new global::Amqp.Session(connection);

        // Must ensure the broker knows this is a topic before sending
        _broker.GetOrCreateTopic("integration-topic");

        var sender = new global::Amqp.SenderLink(session, "topic-sender", "integration-topic");
        await sender.SendAsync(new global::Amqp.Message("Topic message")
        {
            Properties = new global::Amqp.Framing.Properties { MessageId = "topic-1" }
        });
        await sender.CloseAsync();

        var receiverA = new global::Amqp.ReceiverLink(session, "sub-a-recv",
            "integration-topic/Subscriptions/sub-a");
        var msgA = await receiverA.ReceiveAsync(TimeSpan.FromSeconds(5));
        msgA.Should().NotBeNull();
        receiverA.Accept(msgA);
        await receiverA.CloseAsync();

        var receiverB = new global::Amqp.ReceiverLink(session, "sub-b-recv",
            "integration-topic/Subscriptions/sub-b");
        var msgB = await receiverB.ReceiveAsync(TimeSpan.FromSeconds(5));
        msgB.Should().NotBeNull();
        receiverB.Accept(msgB);
        await receiverB.CloseAsync();

        await session.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task Reject_MovesToDlq_ViaAmqp()
    {
        var connection = await CreateConnectionAsync();
        var session = new global::Amqp.Session(connection);

        var sender = new global::Amqp.SenderLink(session, "dlq-sender", "dlq-test-queue");
        await sender.SendAsync(new global::Amqp.Message("DLQ message")
        {
            Properties = new global::Amqp.Framing.Properties { MessageId = "dlq-1" }
        });
        await sender.CloseAsync();

        var receiver = new global::Amqp.ReceiverLink(session, "dlq-receiver", "dlq-test-queue");
        var received = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
        received.Should().NotBeNull();
        receiver.Reject(received);
        await receiver.CloseAsync();

        await session.CloseAsync();
        await connection.CloseAsync();

        var queue = _broker.GetQueue("dlq-test-queue");
        queue.Should().NotBeNull();

        await Task.Delay(500); // allow async DLQ processing
        queue!.DeadLetterCount.Should().Be(1);
    }

    [Fact]
    public async Task AutoCreate_Queue_OnFirstAccess()
    {
        var connection = await CreateConnectionAsync();
        var session = new global::Amqp.Session(connection);

        var sender = new global::Amqp.SenderLink(session, "auto-sender", "auto-created-queue");
        await sender.SendAsync(new global::Amqp.Message("auto-create test")
        {
            Properties = new global::Amqp.Framing.Properties { MessageId = "auto-1" }
        });
        await sender.CloseAsync();

        await session.CloseAsync();
        await connection.CloseAsync();

        _broker.GetQueue("auto-created-queue").Should().NotBeNull();
    }
}

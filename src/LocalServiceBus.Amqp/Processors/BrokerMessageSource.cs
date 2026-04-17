using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Handles outgoing messages to clients (receive path).
/// Implements IMessageSource which ContainerHost uses for receiver links.
/// </summary>
public sealed class BrokerMessageSource : IMessageSource
{
    private readonly MessageBroker _broker;
    private readonly string _entityPath;
    private readonly string? _topicName;
    private readonly string? _subscriptionName;

    public BrokerMessageSource(MessageBroker broker, string entityPath)
    {
        _broker = broker;
        _entityPath = entityPath;

        var idx = entityPath.IndexOf("/Subscriptions/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            _topicName = entityPath[..idx];
            _subscriptionName = entityPath[(idx + "/Subscriptions/".Length)..];
        }
    }

    public async Task<ReceiveContext> GetMessageAsync(ListenerLink link)
    {
        BrokerMessage? msg;

        if (_subscriptionName is not null)
            msg = await _broker.ReceiveFromSubscriptionAsync(_topicName!, _subscriptionName);
        else
            msg = await _broker.ReceiveFromQueueAsync(_entityPath);

        if (msg is null)
            return null!;

        var amqpMessage = AmqpConverter.ToAmqpMessage(msg);
        return new ReceiveContext(link, amqpMessage);
    }

    public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext)
    {
        var lockToken = AmqpConverter.ExtractLockToken(receiveContext.Message);

        try
        {
            if (dispositionContext.DeliveryState is Accepted)
            {
                _broker.CompleteAsync(_entityPath, lockToken).GetAwaiter().GetResult();
            }
            else if (dispositionContext.DeliveryState is Rejected rejected)
            {
                var reason = rejected.Error?.Description ?? "Rejected";
                _broker.DeadLetterAsync(_entityPath, lockToken, reason).GetAwaiter().GetResult();
            }
            else
            {
                _broker.AbandonAsync(_entityPath, lockToken).GetAwaiter().GetResult();
            }

            dispositionContext.Complete();
        }
        catch (Exception)
        {
            dispositionContext.Complete(new Error(ErrorCode.InternalError));
        }
    }
}

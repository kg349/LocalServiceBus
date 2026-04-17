using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Handles incoming messages from clients (send path).
/// Implements IMessageProcessor which ContainerHost routes messages to.
/// </summary>
public sealed class BrokerMessageSink : IMessageProcessor
{
    private readonly MessageBroker _broker;
    private readonly string _entityPath;
    private readonly bool _isTopic;

    public BrokerMessageSink(MessageBroker broker, string entityPath, bool isTopic)
    {
        _broker = broker;
        _entityPath = entityPath;
        _isTopic = isTopic;
    }

    public void Process(MessageContext messageContext)
    {
        try
        {
            var amqpMessage = messageContext.Message;
            var brokerMessage = AmqpConverter.ToBrokerMessage(amqpMessage);

            if (_isTopic)
                _broker.SendToTopicAsync(_entityPath, brokerMessage).GetAwaiter().GetResult();
            else
                _broker.SendToQueueAsync(_entityPath, brokerMessage).GetAwaiter().GetResult();

            messageContext.Complete();
        }
        catch (Exception)
        {
            messageContext.Complete(new Error(ErrorCode.InternalError)
            {
                Description = "Failed to enqueue message"
            });
        }
    }

    public int Credit => 100;
}

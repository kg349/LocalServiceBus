using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using LocalServiceBus.Core.Engine;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Handles dynamic link attach from the Azure SDK.
/// Auto-creates queues/topics/subscriptions on first access.
/// Uses TargetLinkEndpoint (for send) and SourceLinkEndpoint (for receive)
/// wrapping our IMessageProcessor / IMessageSource implementations.
/// </summary>
public sealed class LinkProcessor : ILinkProcessor
{
    private readonly MessageBroker _broker;

    public LinkProcessor(MessageBroker broker)
    {
        _broker = broker;
    }

    public void Process(AttachContext attachContext)
    {
        try
        {
            // The SDK sends ulong.MaxValue as max-message-size meaning "no limit from my side".
            // AMQPNetLite echoes the incoming Attach as the response, so the SDK receives its own
            // ulong.MaxValue, casts it to long (-1), and rejects all messages as "too large".
            // Setting (ulong)long.MaxValue means: > 0 (so SDK applies it), and stays positive
            // when cast back to long, so no practical message ever exceeds the limit.
            attachContext.Attach.MaxMessageSize = (ulong)long.MaxValue;

            if (attachContext.Attach.Role) // role=true means receiver (broker is source)
            {
                var source = attachContext.Attach.Source as Source;
                var entityPath = source?.Address ?? attachContext.Attach.LinkName;
                EnsureEntityExists(entityPath);

                var messageSource = new BrokerMessageSource(_broker, entityPath);
                attachContext.Complete(new SourceLinkEndpoint(messageSource, attachContext.Link), 100);
            }
            else // role=false means sender (broker is target/sink)
            {
                var target = attachContext.Attach.Target as Target;
                var entityPath = target?.Address ?? attachContext.Attach.LinkName;
                var isTopic = DetectTopicOrCreate(entityPath);

                var messageSink = new BrokerMessageSink(_broker, entityPath, isTopic);
                attachContext.Complete(new TargetLinkEndpoint(messageSink, attachContext.Link), 100);
            }
        }
        catch (Exception ex)
        {
            attachContext.Complete(new Error(ErrorCode.InternalError)
            {
                Description = ex.Message
            });
        }
    }

    private void EnsureEntityExists(string entityPath)
    {
        var idx = entityPath.IndexOf("/Subscriptions/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var topicName = entityPath[..idx];
            var subName = entityPath[(idx + "/Subscriptions/".Length)..];
            _broker.GetOrCreateSubscription(topicName, subName);
        }
        else
        {
            _broker.GetOrCreateQueue(entityPath);
        }
    }

    /// <summary>
    /// If the path matches an existing topic, returns true.
    /// Otherwise auto-creates a queue and returns false.
    /// Senders must set up the topic first (via management or config) for topic routing.
    /// </summary>
    private bool DetectTopicOrCreate(string entityPath)
    {
        if (_broker.GetTopic(entityPath) is not null)
            return true;

        _broker.GetOrCreateQueue(entityPath);
        return false;
    }
}

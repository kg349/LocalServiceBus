using Amqp;
using Amqp.Framing;
using LocalServiceBus.Core.Models;
using Symbol = global::Amqp.Types.Symbol;
using ByteBuffer = global::Amqp.ByteBuffer;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Converts between AMQP messages and BrokerMessage instances.
/// </summary>
public static class AmqpConverter
{
    private static readonly Symbol LockTokenSymbol = new("x-opt-lock-token");
    private static readonly Symbol SequenceNumberSymbol = new("x-opt-sequence-number");
    private static readonly Symbol EnqueuedTimeSymbol = new("x-opt-enqueued-time");

    public static BrokerMessage ToBrokerMessage(Message amqpMessage)
    {
        byte[] body;
        if (amqpMessage.Body is byte[] bytes)
            body = bytes;
        else if (amqpMessage.Body is ByteBuffer buffer)
            body = buffer.Buffer;
        else
            body = amqpMessage.Body is not null
                ? System.Text.Encoding.UTF8.GetBytes(amqpMessage.Body.ToString()!)
                : [];

        return new BrokerMessage
        {
            MessageId = amqpMessage.Properties?.MessageId ?? Guid.NewGuid().ToString(),
            Body = body,
            ContentType = amqpMessage.Properties?.ContentType,
            CorrelationId = amqpMessage.Properties?.CorrelationId,
            Subject = amqpMessage.Properties?.Subject,
            To = amqpMessage.Properties?.To,
            ReplyTo = amqpMessage.Properties?.ReplyTo,
            ApplicationProperties = ConvertApplicationProperties(amqpMessage.ApplicationProperties)
        };
    }

    public static Message ToAmqpMessage(BrokerMessage brokerMessage)
    {
        var message = new Message(brokerMessage.Body.ToArray())
        {
            Properties = new Properties
            {
                MessageId = brokerMessage.MessageId,
                ContentType = brokerMessage.ContentType,
                CorrelationId = brokerMessage.CorrelationId,
                Subject = brokerMessage.Subject,
                To = brokerMessage.To,
                ReplyTo = brokerMessage.ReplyTo
            },
            Header = new Header
            {
                DeliveryCount = (uint)brokerMessage.DeliveryCount,
                Ttl = brokerMessage.TimeToLive < TimeSpan.MaxValue
                    ? (uint)brokerMessage.TimeToLive.TotalMilliseconds
                    : 0
            },
            MessageAnnotations = new MessageAnnotations()
        };

        message.MessageAnnotations[LockTokenSymbol] = brokerMessage.LockToken;
        message.MessageAnnotations[SequenceNumberSymbol] = brokerMessage.SequenceNumber;
        message.MessageAnnotations[EnqueuedTimeSymbol] = brokerMessage.EnqueuedTime.UtcDateTime;

        if (brokerMessage.ApplicationProperties.Count > 0)
        {
            message.ApplicationProperties = new ApplicationProperties();
            foreach (var kvp in brokerMessage.ApplicationProperties)
                message.ApplicationProperties[kvp.Key] = kvp.Value;
        }

        return message;
    }

    public static Guid ExtractLockToken(Message message)
    {
        if (message.MessageAnnotations?.Map is not null
            && message.MessageAnnotations.Map.TryGetValue(LockTokenSymbol, out var tokenObj)
            && tokenObj is Guid token)
        {
            return token;
        }

        return Guid.Empty;
    }

    private static Dictionary<string, object> ConvertApplicationProperties(ApplicationProperties? props)
    {
        var dict = new Dictionary<string, object>();
        if (props?.Map is null) return dict;

        foreach (var kvp in props.Map)
        {
            if (kvp.Key is not null && kvp.Value is not null)
                dict[kvp.Key.ToString()!] = kvp.Value;
        }

        return dict;
    }
}

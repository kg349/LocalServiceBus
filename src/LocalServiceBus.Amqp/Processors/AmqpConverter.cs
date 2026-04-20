using Amqp;
using Amqp.Framing;
using LocalServiceBus.Core.Models;
using Symbol = global::Amqp.Types.Symbol;
using ByteBuffer = global::Amqp.ByteBuffer;
using DeliveryAnnotations = global::Amqp.Framing.DeliveryAnnotations;

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
        var lockTokenString = brokerMessage.LockToken.ToString();

        // Use BodySection = Data so the body is encoded as an AMQP data section
        // (binary bytes). If we pass byte[] to the Message constructor directly,
        // AMQPNetLite encodes it as AmqpValue(byte[]) which the Functions binding
        // cannot convert to a string parameter.
        var message = new Message
        {
            BodySection = new Data { Binary = brokerMessage.Body.ToArray() },
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

        // Send the lock token as a string in both sections so it survives
        // encoding across both AMQPNetLite and Microsoft.Azure.Amqp without
        // UUID byte-ordering issues. The Azure SDK parses Guid.Parse(string)
        // as a fallback when the value is not already a Guid.
        message.DeliveryAnnotations = new DeliveryAnnotations();
        message.DeliveryAnnotations[LockTokenSymbol] = lockTokenString;

        message.MessageAnnotations[LockTokenSymbol] = lockTokenString;
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
        // Try MessageAnnotations first — we store the token as a string.
        if (message.MessageAnnotations?.Map is not null
            && message.MessageAnnotations.Map.TryGetValue(LockTokenSymbol, out var tokenObj))
        {
            if (tokenObj is Guid g) return g;
            if (tokenObj is string s && Guid.TryParse(s, out var parsed)) return parsed;
        }

        // Fallback to DeliveryAnnotations.
        if (message.DeliveryAnnotations?.Map is not null
            && message.DeliveryAnnotations.Map.TryGetValue(LockTokenSymbol, out var daTokenObj))
        {
            if (daTokenObj is Guid dag) return dag;
            if (daTokenObj is string das && Guid.TryParse(das, out var daParsed)) return daParsed;
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

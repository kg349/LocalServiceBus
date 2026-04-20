using Amqp;
using Amqp.Handler;
using Amqp.Types;
using System.Reflection;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Intercepts every outgoing AMQP Transfer and overwrites the delivery tag with the
/// 16-byte representation of the broker lock token that we embedded in DeliveryAnnotations.
///
/// Why this is required
/// --------------------
/// Azure.Messaging.ServiceBus reads the lock token ONLY from the AMQP delivery tag:
///
///   if (GuidUtilities.TryParseGuidBytes(amqpMessage.DeliveryTag, out Guid lockToken))
///       sbMessage.LockTokenGuid = lockToken;
///
/// AMQPNetLite normally auto-generates a 4-byte sequential uint as the delivery tag.
/// A 4-byte array cannot be parsed as a 16-byte Guid, so LockTokenGuid stays Guid.Empty
/// and the SDK throws "This operation is not supported for peeked messages" whenever it
/// tries to renew or settle the lock.
///
/// AMQPNetLite exposes a hook via ConnectionListener.HandlerFactory → IHandler.Handle.
/// Inside ListenerLink.SendMessageInternal the pattern is:
///
///   handler.Handle(Event.Create(EventId.SendDelivery, ..., context: delivery));
///   if (delivery.Tag == null)
///       delivery.Tag = Delivery.GetDeliveryTag(tag);   // 4-byte fallback
///
/// We set delivery.Tag to the 16-byte Guid bytes BEFORE the fallback runs.
/// Because Amqp.Delivery is an internal class we reach Tag via cached reflection.
/// </summary>
internal sealed class DeliveryTagHandler : IHandler
{
    private static readonly Symbol LockTokenKey = new("x-opt-lock-token");

    // Amqp.Delivery is internal — access properties via cached reflection.
    private static readonly Type? _deliveryType =
        typeof(Message).Assembly.GetType("Amqp.Delivery");

    private static readonly PropertyInfo? _tagProperty =
        _deliveryType?.GetProperty("Tag",
            BindingFlags.Public | BindingFlags.Instance);

    private static readonly PropertyInfo? _messageProperty =
        _deliveryType?.GetProperty("Message",
            BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// Fails fast at startup if the AMQPNetLite internals have changed after a library upgrade.
    /// Without both reflection targets the delivery tag stays 4 bytes and the lock token will
    /// always be Guid.Empty in the Azure SDK, breaking settlement and lock renewal for every
    /// received message.
    /// </summary>
    public DeliveryTagHandler()
    {
        if (_tagProperty is null || _messageProperty is null)
        {
            throw new InvalidOperationException(
                "DeliveryTagHandler: AMQPNetLite reflection targets not found. " +
                "Expected Amqp.Delivery.Tag (byte[]) and Amqp.Delivery.Message (Amqp.Message) " +
                "on the internal Amqp.Delivery class. " +
                "This typically means AMQPNetLite was upgraded and the internal class was renamed or restructured. " +
                "Check the AMQPNetLite changelog and update the property name(s) in DeliveryTagHandler.cs.");
        }
    }

    public bool CanHandle(EventId id) => id == EventId.SendDelivery;

    public void Handle(Event evnt)
    {
        try
        {
            if (evnt.Id != EventId.SendDelivery) return;

            var context = evnt.Context;
            if (context is null || _tagProperty is null || _messageProperty is null) return;

            var message = _messageProperty.GetValue(context) as Message;
            if (message?.DeliveryAnnotations is null) return;

            // Read the string lock token we stored in DeliveryAnnotations.
            var lockTokenObj = message.DeliveryAnnotations[LockTokenKey];
            if (lockTokenObj is not string lockTokenStr) return;
            if (!Guid.TryParse(lockTokenStr, out Guid lockToken)) return;

            // Set the delivery tag to exactly 16 bytes (Guid).
            // GuidUtilities.TryParseGuidBytes in the Azure SDK calls new Guid(bytes),
            // which is the inverse of Guid.ToByteArray(), so the round-trip is exact.
            _tagProperty.SetValue(context, lockToken.ToByteArray());
        }
        catch
        {
            // Never disrupt the send path — worst case the 4-byte fallback is used.
        }
    }
}

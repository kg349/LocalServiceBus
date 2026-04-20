using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Amqp.Processors;

namespace LocalServiceBus.Amqp;

public sealed class AmqpListenerHost : IDisposable
{
    private readonly ContainerHost _host;
    private readonly MessageBroker _broker;
    private readonly LinkProcessor _linkProcessor;

    public AmqpListenerHost(MessageBroker broker, int port = 5672)
    {
        _broker = broker;

        var address = new Address($"amqp://localhost:{port}");
        _host = new ContainerHost(address);

        // Register a per-connection handler that rewrites the AMQP delivery tag to the
        // 16-byte lock token Guid before AMQPNetLite applies its 4-byte sequential fallback.
        // The Azure SDK reads LockTokenGuid exclusively from the delivery tag; a 4-byte tag
        // cannot be parsed as a Guid, leaving LockTokenGuid = Guid.Empty and causing the
        // "peeked messages" error on every renewal/settlement attempt.
        var deliveryTagHandler = new DeliveryTagHandler();

        // Register SASL mechanisms for all supported SDK versions:
        //   MSSBCBS  — Azure.Messaging.ServiceBus (v7+) with UseDevelopmentEmulator=true
        //   ANONYMOUS — Microsoft.Azure.ServiceBus (v4, old SDK) and any AMQP client that
        //               does not send credentials (CBS handles authorization after SASL)
        foreach (var listener in _host.Listeners)
        {
            listener.HandlerFactory = _ => deliveryTagHandler;
            listener.SASL.EnableMechanism(new Symbol("MSSBCBS"), new MssbCbsSaslProfile());
            listener.SASL.EnableAnonymousMechanism = true;
        }

        // After SASL the SDK opens a $cbs link to exchange a SAS token.
        // We accept all tokens unconditionally for local development.
        _host.RegisterRequestProcessor("$cbs", new CbsRequestProcessor());

        // Handle $management requests (e.g. lock renewal from the Functions trigger).
        // The SDK may address it as "{entity}/$management", so we normalise all
        // management addresses to the single registered "$management" processor.
        _host.RegisterRequestProcessor("$management", new ManagementRequestProcessor(broker));
        _host.AddressResolver = (_, attach) =>
        {
            var address = attach.Role
                ? (attach.Source as Source)?.Address
                : (attach.Target as Target)?.Address;

            if (address != null &&
                address.EndsWith("/$management", StringComparison.OrdinalIgnoreCase))
            {
                return "$management";
            }

            return null;
        };

        _linkProcessor = new LinkProcessor(broker);
        _host.RegisterLinkProcessor(_linkProcessor);
    }

    public void Start()
    {
        _host.Open();
    }

    public void Stop()
    {
        _host.Close();
    }

    public void Dispose()
    {
        Stop();
    }
}

using Amqp;
using Amqp.Listener;
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
        var address = new Address($"amqp://guest:guest@localhost:{port}");
        _host = new ContainerHost(address);

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

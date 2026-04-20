using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using Amqp.Types;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Server-side SASL profile for the "MSSBCBS" mechanism used by the
/// Azure Service Bus SDK when UseDevelopmentEmulator=true is set.
/// MSSBCBS is Microsoft's proprietary mechanism — functionally identical
/// to ANONYMOUS (no challenge/response). We accept everything unconditionally.
/// </summary>
public sealed class MssbCbsSaslProfile : SaslProfile
{
    public MssbCbsSaslProfile() : base(new Symbol("MSSBCBS")) { }

    // No transport upgrade needed (not TLS)
    protected override ITransport UpgradeTransport(ITransport transport) => transport;

    // Server never initiates — only used on the client side
    protected override DescribedList GetStartCommand(string hostname) => null!;

    // Called when the client sends SaslInit — accept unconditionally
    protected override DescribedList OnCommand(DescribedList command)
    {
        return new SaslOutcome { Code = SaslCode.Ok };
    }
}

using Amqp;
using Amqp.Framing;
using Amqp.Listener;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Handles the $cbs (Claims-Based Security) token exchange that the
/// Azure Service Bus SDK performs immediately after connecting.
/// We accept all tokens unconditionally — auth is not required locally.
/// </summary>
public sealed class CbsRequestProcessor : IRequestProcessor
{
    public int Credit => 100;

    public void Process(RequestContext requestContext)
    {
        var response = new Message("accepted")
        {
            Properties = new Properties
            {
                CorrelationId = requestContext.Message.Properties?.MessageId
            },
            ApplicationProperties = new ApplicationProperties()
        };

        response.ApplicationProperties["status-code"] = 200;
        response.ApplicationProperties["status-description"] = "OK";

        requestContext.Complete(response);
    }
}

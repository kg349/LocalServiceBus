using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using LocalServiceBus.Core.Engine;

namespace LocalServiceBus.Amqp.Processors;

/// <summary>
/// Handles AMQP $management requests.
/// Currently supports lock renewal (com.microsoft:renew-lock-for-message),
/// which the Azure Functions Service Bus trigger sends periodically to keep
/// peek-locks alive while a function is executing.
/// </summary>
public sealed class ManagementRequestProcessor : IRequestProcessor
{
    private static readonly Symbol OperationKey = new("operation");
    private static readonly Symbol RenewLockOperation = new("com.microsoft:renew-lock-for-message");

    private readonly MessageBroker _broker;

    public ManagementRequestProcessor(MessageBroker broker)
    {
        _broker = broker;
    }

    public int Credit => 100;

    public void Process(RequestContext requestContext)
    {
        var request = requestContext.Message;

        var operation = GetOperation(request);

        Message response = operation == RenewLockOperation.ToString()
            ? HandleRenewLock(request)
            : CreateResponse(request, 501, $"Operation '{operation}' is not supported by the local emulator.");

        requestContext.Complete(response);
    }

    private Message HandleRenewLock(Message request)
    {
        try
        {
            var lockTokens = ExtractLockTokens(request.Body);
            var expirations = new object[lockTokens.Count];

            for (int i = 0; i < lockTokens.Count; i++)
            {
                var newExpiry = _broker.RenewLock(lockTokens[i]);
                expirations[i] = newExpiry.UtcDateTime;
            }

            var responseBody = new Map { ["expirations"] = expirations };
            return CreateResponse(request, 200, "OK", responseBody);
        }
        catch (Exception ex)
        {
            return CreateResponse(request, 500, ex.Message);
        }
    }

    private static List<Guid> ExtractLockTokens(object? body)
    {
        var tokens = new List<Guid>();

        if (body is not Map map) return tokens;

        if (!map.TryGetValue("lock-tokens", out var raw)) return tokens;

        if (raw is Guid singleGuid)
        {
            tokens.Add(singleGuid);
        }
        else if (raw is object[] arr)
        {
            foreach (var item in arr)
            {
                if (item is Guid g) tokens.Add(g);
                else if (item is string s && Guid.TryParse(s, out var parsed)) tokens.Add(parsed);
            }
        }

        return tokens;
    }

    private static Message CreateResponse(Message request, int statusCode, string description,
        object? body = null)
    {
        var response = new Message(body ?? string.Empty)
        {
            Properties = new Properties
            {
                CorrelationId = request.Properties?.MessageId
            },
            ApplicationProperties = new ApplicationProperties()
        };

        response.ApplicationProperties["status-code"] = statusCode;
        response.ApplicationProperties["status-description"] = description;

        return response;
    }

    private static string? GetOperation(Message request)
    {
        if (request.ApplicationProperties?.Map is null) return null;
        return request.ApplicationProperties.Map.TryGetValue(OperationKey, out var op)
            ? op?.ToString()
            : null;
    }
}

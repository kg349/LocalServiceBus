using System.Text.Json;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalServiceBus.Management;

public static class ManagementApi
{
    public static void MapManagementEndpoints(this IEndpointRouteBuilder app, MessageBroker broker)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

        app.MapPost("/reset", () =>
        {
            broker.Reset();
            return Results.Ok(new { message = "All state cleared" });
        });

        // Queue endpoints
        app.MapGet("/queues", () =>
        {
            var stats = broker.GetStats();
            return Results.Ok(stats.Queues);
        });

        app.MapGet("/queues/{name}", (string name) =>
        {
            var queue = broker.GetQueue(name);
            if (queue is null) return Results.NotFound();
            return Results.Ok(new
            {
                name,
                activeMessages = queue.ActiveMessageCount,
                deadLetterMessages = queue.DeadLetterCount,
                createdAt = queue.Entity.CreatedAt
            });
        });

        app.MapPost("/queues/{name}/messages", async (string name, HttpRequest request) =>
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            var message = new BrokerMessage
            {
                Body = System.Text.Encoding.UTF8.GetBytes(body),
                ContentType = request.ContentType ?? "application/json"
            };
            await broker.SendToQueueAsync(name, message);
            return Results.Accepted(value: new { messageId = message.MessageId });
        });

        app.MapDelete("/queues/{name}/messages", (string name) =>
        {
            var queue = broker.GetQueue(name);
            if (queue is null) return Results.NotFound();
            queue.Purge();
            return Results.Ok(new { message = "Queue purged" });
        });

        app.MapGet("/queues/{name}/deadletter", async (string name, int? maxCount) =>
        {
            var queue = broker.GetQueue(name);
            if (queue is null) return Results.NotFound();
            var messages = await queue.DeadLetterQueue.PeekAsync(maxCount ?? 10);
            return Results.Ok(messages.Select(m => new
            {
                m.MessageId,
                m.DeadLetterReason,
                m.DeadLetterErrorDescription,
                m.DeliveryCount,
                m.EnqueuedTime,
                body = System.Text.Encoding.UTF8.GetString(m.Body.Span)
            }));
        });

        app.MapPost("/queues/{name}/deadletter/resubmit", async (string name) =>
        {
            var queue = broker.GetQueue(name);
            if (queue is null) return Results.NotFound();
            await queue.ResubmitDeadLettersAsync();
            return Results.Ok(new { message = "Dead letters resubmitted" });
        });

        // Topic endpoints
        app.MapGet("/topics", () =>
        {
            var stats = broker.GetStats();
            return Results.Ok(stats.Topics);
        });

        app.MapGet("/topics/{name}/subscriptions", (string name) =>
        {
            var topic = broker.GetTopic(name);
            if (topic is null) return Results.NotFound();
            return Results.Ok(topic.GetSubscriptionNames().Select(s =>
            {
                var sub = topic.GetSubscription(s)!;
                return new
                {
                    name = s,
                    activeMessages = sub.ActiveMessageCount,
                    deadLetterMessages = sub.DeadLetterCount
                };
            }));
        });

        app.MapPost("/topics/{name}/messages", async (string name, HttpRequest request) =>
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            var message = new BrokerMessage
            {
                Body = System.Text.Encoding.UTF8.GetBytes(body),
                ContentType = request.ContentType ?? "application/json"
            };
            await broker.SendToTopicAsync(name, message);
            return Results.Accepted(value: new { messageId = message.MessageId });
        });
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using LocalServiceBus.Core.Engine;

namespace LocalServiceBus;

public static class ConfigLoader
{
    public static void LoadConfig(MessageBroker broker, string json)
    {
        var config = JsonSerializer.Deserialize<EmulatorConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config is null) return;

        foreach (var q in config.Queues ?? [])
        {
            broker.GetOrCreateQueue(q.Name, q.MaxDeliveryCount);
        }

        foreach (var t in config.Topics ?? [])
        {
            broker.GetOrCreateTopic(t.Name);
            foreach (var s in t.Subscriptions ?? [])
            {
                broker.GetOrCreateSubscription(t.Name, s.Name, s.MaxDeliveryCount);
            }
        }
    }
}

public class EmulatorConfig
{
    public List<QueueConfig>? Queues { get; set; }
    public List<TopicConfig>? Topics { get; set; }
}

public class QueueConfig
{
    public required string Name { get; set; }
    public int MaxDeliveryCount { get; set; } = 10;
}

public class TopicConfig
{
    public required string Name { get; set; }
    public List<SubscriptionConfig>? Subscriptions { get; set; }
}

public class SubscriptionConfig
{
    public required string Name { get; set; }
    public int MaxDeliveryCount { get; set; } = 10;
}

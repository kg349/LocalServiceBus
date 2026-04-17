using System.Collections.Concurrent;
using System.Threading.Channels;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Store;

public class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<string, Channel<BrokerMessage>> _channels = new();

    private Channel<BrokerMessage> GetChannel(string entityPath)
    {
        return _channels.GetOrAdd(entityPath, _ => Channel.CreateUnbounded<BrokerMessage>());
    }

    public async Task SaveMessageAsync(string entityPath, BrokerMessage message)
    {
        await GetChannel(entityPath).Writer.WriteAsync(message);
    }

    public async Task<BrokerMessage?> LoadNextMessageAsync(string entityPath)
    {
        var channel = GetChannel(entityPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            return await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Task DeleteMessageAsync(string entityPath, string messageId) => Task.CompletedTask;

    public Task<List<BrokerMessage>> PeekMessagesAsync(string entityPath, int maxCount = 10)
    {
        return Task.FromResult(new List<BrokerMessage>());
    }

    public Task PurgeAsync(string entityPath)
    {
        if (_channels.TryGetValue(entityPath, out var channel))
        {
            while (channel.Reader.TryRead(out _)) { }
        }
        return Task.CompletedTask;
    }

    public Task ResetAsync()
    {
        _channels.Clear();
        return Task.CompletedTask;
    }
}

using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Store;

public interface IMessageStore
{
    Task SaveMessageAsync(string entityPath, BrokerMessage message);
    Task<BrokerMessage?> LoadNextMessageAsync(string entityPath);
    Task DeleteMessageAsync(string entityPath, string messageId);
    Task<List<BrokerMessage>> PeekMessagesAsync(string entityPath, int maxCount = 10);
    Task PurgeAsync(string entityPath);
    Task ResetAsync();
}

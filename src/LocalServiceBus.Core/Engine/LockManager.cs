using System.Collections.Concurrent;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Engine;

public sealed class LockManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, LockedMessage> _locks = new();
    private readonly Timer _expirationTimer;
    private readonly Action<BrokerMessage> _onLockExpired;

    public LockManager(Action<BrokerMessage> onLockExpired)
    {
        _onLockExpired = onLockExpired;
        _expirationTimer = new Timer(EvictExpiredLocks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public Guid Lock(BrokerMessage message, TimeSpan duration)
    {
        var token = Guid.NewGuid();
        var locked = new LockedMessage(message, DateTimeOffset.UtcNow.Add(duration));
        _locks[token] = locked;
        message.LockToken = token;
        return token;
    }

    public BrokerMessage? Complete(Guid lockToken)
    {
        return _locks.TryRemove(lockToken, out var locked) ? locked.Message : null;
    }

    public BrokerMessage? Abandon(Guid lockToken)
    {
        if (!_locks.TryRemove(lockToken, out var locked))
            return null;

        locked.Message.DeliveryCount++;
        return locked.Message;
    }

    public BrokerMessage? GetLockedMessage(Guid lockToken)
    {
        return _locks.TryGetValue(lockToken, out var locked) ? locked.Message : null;
    }

    public DateTimeOffset RenewLock(Guid lockToken, TimeSpan duration)
    {
        if (!_locks.TryGetValue(lockToken, out var locked))
            throw new InvalidOperationException($"Lock token {lockToken} not found or expired.");

        var newExpiry = DateTimeOffset.UtcNow.Add(duration);
        _locks[lockToken] = locked with { ExpiresAt = newExpiry };
        return newExpiry;
    }

    public int ActiveLockCount => _locks.Count;

    private void EvictExpiredLocks(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _locks)
        {
            if (kvp.Value.ExpiresAt > now) continue;
            if (!_locks.TryRemove(kvp.Key, out var expired)) continue;

            expired.Message.DeliveryCount++;
            _onLockExpired(expired.Message);
        }
    }

    public void Dispose()
    {
        _expirationTimer.Dispose();
    }

    private sealed record LockedMessage(BrokerMessage Message, DateTimeOffset ExpiresAt);
}

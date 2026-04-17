using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalServiceBus.Core.Models;

namespace LocalServiceBus.Core.Store;

public sealed class SqliteMessageStore : IMessageStore, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteMessageStore(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EntityPath TEXT NOT NULL,
                MessageId TEXT NOT NULL,
                Body BLOB NOT NULL,
                ContentType TEXT,
                CorrelationId TEXT,
                Subject TEXT,
                DeliveryCount INTEGER DEFAULT 0,
                EnqueuedTime TEXT NOT NULL,
                ApplicationProperties TEXT,
                DeadLetterReason TEXT,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS IX_Messages_EntityPath ON Messages(EntityPath);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveMessageAsync(string entityPath, BrokerMessage message)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Messages (EntityPath, MessageId, Body, ContentType, CorrelationId, Subject, DeliveryCount, EnqueuedTime, ApplicationProperties)
            VALUES (@entityPath, @messageId, @body, @contentType, @correlationId, @subject, @deliveryCount, @enqueuedTime, @appProps)
            """;
        cmd.Parameters.AddWithValue("@entityPath", entityPath);
        cmd.Parameters.AddWithValue("@messageId", message.MessageId);
        cmd.Parameters.AddWithValue("@body", message.Body.ToArray());
        cmd.Parameters.AddWithValue("@contentType", (object?)message.ContentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@correlationId", (object?)message.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@subject", (object?)message.Subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@deliveryCount", message.DeliveryCount);
        cmd.Parameters.AddWithValue("@enqueuedTime", message.EnqueuedTime.ToString("O"));
        cmd.Parameters.AddWithValue("@appProps", JsonSerializer.Serialize(message.ApplicationProperties));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<BrokerMessage?> LoadNextMessageAsync(string entityPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, MessageId, Body, ContentType, CorrelationId, Subject, DeliveryCount, EnqueuedTime, ApplicationProperties
            FROM Messages WHERE EntityPath = @entityPath ORDER BY Id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@entityPath", entityPath);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var id = reader.GetInt64(0);
        var msg = ReadMessage(reader);

        using var deleteCmd = _connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM Messages WHERE Id = @id";
        deleteCmd.Parameters.AddWithValue("@id", id);
        await deleteCmd.ExecuteNonQueryAsync();

        return msg;
    }

    public async Task DeleteMessageAsync(string entityPath, string messageId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Messages WHERE EntityPath = @entityPath AND MessageId = @messageId";
        cmd.Parameters.AddWithValue("@entityPath", entityPath);
        cmd.Parameters.AddWithValue("@messageId", messageId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<BrokerMessage>> PeekMessagesAsync(string entityPath, int maxCount = 10)
    {
        var messages = new List<BrokerMessage>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, MessageId, Body, ContentType, CorrelationId, Subject, DeliveryCount, EnqueuedTime, ApplicationProperties
            FROM Messages WHERE EntityPath = @entityPath ORDER BY Id LIMIT @maxCount
            """;
        cmd.Parameters.AddWithValue("@entityPath", entityPath);
        cmd.Parameters.AddWithValue("@maxCount", maxCount);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            messages.Add(ReadMessage(reader));

        return messages;
    }

    public async Task PurgeAsync(string entityPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Messages WHERE EntityPath = @entityPath";
        cmd.Parameters.AddWithValue("@entityPath", entityPath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ResetAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Messages";
        await cmd.ExecuteNonQueryAsync();
    }

    private static BrokerMessage ReadMessage(SqliteDataReader reader)
    {
        var appProps = reader.IsDBNull(8) ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(8)) ?? new();

        return new BrokerMessage
        {
            MessageId = reader.GetString(1),
            Body = (byte[])reader[2],
            ContentType = reader.IsDBNull(3) ? null : reader.GetString(3),
            CorrelationId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Subject = reader.IsDBNull(5) ? null : reader.GetString(5),
            DeliveryCount = reader.GetInt32(6),
            EnqueuedTime = DateTimeOffset.Parse(reader.GetString(7)),
            ApplicationProperties = appProps
        };
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

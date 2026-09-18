using System.Text.Json;
using Microsoft.Data.Sqlite;
using QQAlchemyDesktop.Domain;

namespace QQAlchemyDesktop.Infrastructure;

public sealed class SqliteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteStore(AppPaths paths) => _paths = paths;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_paths.Database};Cache=Shared;Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS inventory (
                    herb_name TEXT PRIMARY KEY,
                    count INTEGER NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS checkpoint (
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at TEXT NOT NULL,
                    level TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    action_id TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_audit_occurred_at ON audit(occurred_at DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> GetSettingAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            var value = await command.ExecuteScalarAsync(cancellationToken) as string;
            return value is null ? default : JsonSerializer.Deserialize<T>(value, JsonOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSettingAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key,json,updated_at) VALUES($key,$json,$now)
                ON CONFLICT(key) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AutomationCheckpoint> LoadCheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM checkpoint WHERE id=1";
            var value = await command.ExecuteScalarAsync(cancellationToken) as string;
            var checkpoint = value is null
                ? new AutomationCheckpoint()
                : JsonSerializer.Deserialize<AutomationCheckpoint>(value, JsonOptions) ?? new AutomationCheckpoint();

            if (checkpoint.State is not AutomationState.Idle and not AutomationState.Completed)
            {
                checkpoint.State = AutomationState.PausedRecovery;
                checkpoint.Step = "检测到未完成任务，等待人工确认";
                checkpoint.PendingActionId = null;
                checkpoint.UpdatedAt = DateTimeOffset.Now;
            }
            return checkpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCheckpointAsync(AutomationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        checkpoint.UpdatedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO checkpoint(id,json,updated_at) VALUES(1,$json,$now)
                ON CONFLICT(id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, int>> LoadInventoryAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT herb_name,count FROM inventory ORDER BY herb_name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveInventoryAsync(IReadOnlyDictionary<string, int> inventory, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM inventory";
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var pair in inventory)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO inventory(herb_name,count,updated_at) VALUES($name,$count,$now)";
                insert.Parameters.AddWithValue("$name", pair.Key);
                insert.Parameters.AddWithValue("$count", pair.Value);
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AuditAsync(string level, string eventType, string detail, string? actionId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO audit(occurred_at,level,event_type,detail,action_id) VALUES($at,$level,$type,$detail,$action)";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$level", level);
            command.Parameters.AddWithValue("$type", eventType);
            command.Parameters.AddWithValue("$detail", detail);
            command.Parameters.AddWithValue("$action", (object?)actionId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<object>> RecentAuditAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        var rows = new List<object>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT occurred_at,level,event_type,detail,action_id FROM audit ORDER BY id DESC LIMIT $count";
            command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 500));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new
                {
                    occurredAt = reader.GetString(0), level = reader.GetString(1), eventType = reader.GetString(2),
                    detail = reader.GetString(3), actionId = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }
}

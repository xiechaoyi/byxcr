using Byxcr.Core;
using Microsoft.Data.Sqlite;

namespace Byxcr.Persistence;

/// <summary>同步记录表 sync_records 的写入与查询。</summary>
public sealed class SyncRecordStore
{
    private const string Columns =
        "id, image_id, image, status, channel, registry, digest, file_path, file_size, duration_ms, message, trigger_by, started_at, finished_at";

    private readonly SqliteDatabase _db;

    public SyncRecordStore(SqliteDatabase db) => _db = db;

    public void Add(SyncRecord record)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_records
                (image_id, image, status, channel, registry, digest, file_path, file_size, duration_ms, message, trigger_by, started_at, finished_at)
            VALUES
                ($image_id, $image, $status, $channel, $registry, $digest, $file_path, $file_size, $duration_ms, $message, $trigger_by, $started_at, $finished_at)
            """;
        command.Parameters.AddWithValue("$image_id", record.ImageId);
        command.Parameters.AddWithValue("$image", record.Image);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$channel", (object?)record.Channel ?? DBNull.Value);
        command.Parameters.AddWithValue("$registry", (object?)record.Registry ?? DBNull.Value);
        command.Parameters.AddWithValue("$digest", (object?)record.Digest ?? DBNull.Value);
        command.Parameters.AddWithValue("$file_path", (object?)record.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$file_size", (object?)record.FileSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration_ms", (object?)record.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)record.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$trigger_by", (object?)record.Trigger ?? DBNull.Value);
        command.Parameters.AddWithValue("$started_at", record.StartedAt);
        command.Parameters.AddWithValue("$finished_at", (object?)record.FinishedAt ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public List<SyncRecord> Recent(int limit, string? image = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        var sql = $"SELECT {Columns} FROM sync_records";
        if (!string.IsNullOrWhiteSpace(image) && ImageReference.TryParse(image, out var reference))
        {
            sql += " WHERE image = $image";
            command.Parameters.AddWithValue("$image", reference.CanonicalName);
        }
        command.CommandText = sql + " ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));

        using var reader = command.ExecuteReader();
        var list = new List<SyncRecord>();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    /// <summary>清理历史记录，仅保留每个镜像最近 keepPerImage 条。</summary>
    public int Prune(int keepPerImage = 200)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sync_records
            WHERE id NOT IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY image_id ORDER BY id DESC) AS rn
                    FROM sync_records
                ) WHERE rn <= $keep
            )
            """;
        command.Parameters.AddWithValue("$keep", Math.Max(1, keepPerImage));
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除 <paramref name="days"/> 天之前的同步记录（按 <c>started_at</c> 的 UTC 文本比较，
    /// 该列固定为 ISO-8601 格式，字典序即时间序）；传入 <paramref name="image"/> 时只清该镜像。返回删除条数。
    /// </summary>
    public int DeleteOlderThan(int days, string? image = null)
    {
        var cutoff = Clock.Format(DateTime.UtcNow.AddDays(-Math.Max(1, days)));

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        var sql = "DELETE FROM sync_records WHERE started_at < $cutoff";
        if (!string.IsNullOrWhiteSpace(image) && ImageReference.TryParse(image, out var reference))
        {
            sql += " AND image = $image";
            command.Parameters.AddWithValue("$image", reference.CanonicalName);
        }

        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    public int Count()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sync_records";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SyncRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ImageId = reader.GetInt64(1),
        Image = reader.GetString(2),
        Status = reader.GetString(3),
        Channel = reader.IsDBNull(4) ? null : reader.GetString(4),
        Registry = reader.IsDBNull(5) ? null : reader.GetString(5),
        Digest = reader.IsDBNull(6) ? null : reader.GetString(6),
        FilePath = reader.IsDBNull(7) ? null : reader.GetString(7),
        FileSize = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        DurationMs = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        Message = reader.IsDBNull(10) ? null : reader.GetString(10),
        Trigger = reader.IsDBNull(11) ? null : reader.GetString(11),
        StartedAt = reader.GetString(12),
        FinishedAt = reader.IsDBNull(13) ? null : reader.GetString(13),
    };
}

using Byxcr.Core;
using Microsoft.Data.Sqlite;

namespace Byxcr.Persistence;

/// <summary>镜像同步任务表 images 的读写。</summary>
public sealed class ImageStore
{
    private const string Columns =
        "id, image, repository, tag, interval_seconds, enabled, created_at, updated_at, " +
        "last_checked_at, last_success_at, last_digest, last_status, last_error, last_file, output";

    private readonly SqliteDatabase _db;

    public ImageStore(SqliteDatabase db) => _db = db;

    public List<ImageTask> GetAll()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM images ORDER BY id";
        using var reader = command.ExecuteReader();
        var list = new List<ImageTask>();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    /// <summary>
    /// list 用：按添加时间倒序（新添加的在前），可按镜像名关键字与启用状态筛选（忽略大小写），
    /// 从第 <paramref name="offset"/> 条开始最多返回 <paramref name="limit"/> 条（分页）。
    /// <paramref name="filter"/> 为空表示不筛选关键字，<paramref name="enabled"/> 为 null 表示不限启用状态。
    /// </summary>
    public List<ImageTask> Query(string? filter, int limit, int offset = 0, bool? enabled = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM images WHERE {FilterClause} ORDER BY created_at DESC, id DESC LIMIT $limit OFFSET $offset";
        AddFilter(command, filter, enabled);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        using var reader = command.ExecuteReader();
        var list = new List<ImageTask>();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    /// <summary>
    /// 符合筛选条件的任务总数（用于「共 N 条」与分页总页数）。
    /// <paramref name="filter"/> 为空表示不限关键字，<paramref name="enabled"/> 为 null 表示不限启用状态。
    /// </summary>
    public int CountMatching(string? filter, bool? enabled = null)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM images WHERE {FilterClause}";
        AddFilter(command, filter, enabled);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>筛选条件：参数为 NULL 时该项不过滤，否则按镜像名包含关键字（忽略大小写）与启用状态过滤。</summary>
    private const string FilterClause =
        "($filter IS NULL OR instr(lower(image), lower($filter)) > 0) AND ($enabled IS NULL OR enabled = $enabled)";

    private static void AddFilter(SqliteCommand command, string? filter, bool? enabled)
    {
        command.Parameters.AddWithValue(
            "$filter",
            string.IsNullOrWhiteSpace(filter) ? DBNull.Value : filter.Trim());
        command.Parameters.AddWithValue("$enabled", enabled is null ? DBNull.Value : enabled.Value ? 1 : 0);
    }

    public ImageTask? Find(string image)
    {
        if (!ImageReference.TryParse(image, out var reference)) return null;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM images WHERE image = $image";
        command.Parameters.AddWithValue("$image", reference.CanonicalName);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public ImageTask? FindById(long id)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM images WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>新增或更新镜像任务；overwriteSettings 为 true 时用传入的间隔/启用状态覆盖已有记录。</summary>
    public void Upsert(ImageTask task, bool overwriteSettings)
    {
        var now = Clock.Now();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();

        if (overwriteSettings)
        {
            command.CommandText = """
                INSERT INTO images (image, repository, tag, interval_seconds, enabled, created_at, updated_at, last_status, output)
                VALUES ($image, $repository, $tag, $interval, $enabled, $now, $now, 'idle', $output)
                ON CONFLICT(image) DO UPDATE SET
                    repository       = excluded.repository,
                    tag              = excluded.tag,
                    interval_seconds = excluded.interval_seconds,
                    enabled          = excluded.enabled,
                    updated_at       = excluded.updated_at,
                    -- 再次 add 没带 --output 时保留原有输出位置
                    output           = COALESCE(excluded.output, images.output)
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO images (image, repository, tag, interval_seconds, enabled, created_at, updated_at, last_status, output)
                VALUES ($image, $repository, $tag, $interval, $enabled, $now, $now, 'idle', $output)
                ON CONFLICT(image) DO UPDATE SET
                    repository = excluded.repository,
                    tag        = excluded.tag,
                    updated_at = excluded.updated_at
                """;
        }

        command.Parameters.AddWithValue("$image", task.Image);
        command.Parameters.AddWithValue("$repository", task.Repository);
        command.Parameters.AddWithValue("$tag", task.Tag);
        command.Parameters.AddWithValue("$interval", Math.Max(1, task.IntervalSeconds));
        command.Parameters.AddWithValue("$enabled", task.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$output", (object?)task.Output ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public bool Delete(string image)
    {
        if (!ImageReference.TryParse(image, out var reference)) return false;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM images WHERE image = $image";
        command.Parameters.AddWithValue("$image", reference.CanonicalName);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetEnabled(string image, bool enabled)
    {
        if (!ImageReference.TryParse(image, out var reference)) return false;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET enabled = $enabled, updated_at = $now WHERE image = $image";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", Clock.Now());
        command.Parameters.AddWithValue("$image", reference.CanonicalName);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>单独改输出位置（归档路径）；传 null / 空白表示清掉，回到默认归档规则。</summary>
    public bool SetOutput(string image, string? output)
    {
        if (!ImageReference.TryParse(image, out var reference)) return false;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET output = $output, updated_at = $now WHERE image = $image";
        command.Parameters.AddWithValue("$output", string.IsNullOrWhiteSpace(output) ? DBNull.Value : output.Trim());
        command.Parameters.AddWithValue("$now", Clock.Now());
        command.Parameters.AddWithValue("$image", reference.CanonicalName);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetInterval(string image, int intervalSeconds)
    {
        if (!ImageReference.TryParse(image, out var reference)) return false;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET interval_seconds = $interval, updated_at = $now WHERE image = $image";
        command.Parameters.AddWithValue("$interval", Math.Max(1, intervalSeconds));
        command.Parameters.AddWithValue("$now", Clock.Now());
        command.Parameters.AddWithValue("$image", reference.CanonicalName);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>记录一次检查结果。</summary>
    public void MarkChecked(long id, string status, string? error, string? digest, string? file, string at, bool success)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = success
            ? """
              UPDATE images SET
                  last_checked_at = $checked,
                  last_status     = $status,
                  last_error      = NULL,
                  last_digest     = COALESCE($digest, last_digest),
                  last_file       = COALESCE($file, last_file),
                  last_success_at = CASE WHEN $status = 'success' THEN $checked ELSE last_success_at END,
                  updated_at      = $checked
              WHERE id = $id
              """
            : """
              UPDATE images SET
                  last_checked_at = $checked,
                  last_status     = $status,
                  last_error      = $error,
                  updated_at      = $checked
              WHERE id = $id
              """;
        command.Parameters.AddWithValue("$checked", at);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$digest", (object?)digest ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", (object?)file ?? DBNull.Value);
        if (!success) command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        else command.Parameters.AddWithValue("$error", DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void SetStatus(long id, string status)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET last_status = $status WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 申请「正在同步」租约（替代原来的 .lock 文件），持有者与时间写在 images 表里。
    /// 空闲、被自己占用、或上次占用已超过 <paramref name="holdSeconds"/> 秒（进程异常退出留下的残留）时都能抢到；
    /// 被别人占用且未超时则抢不到。id &lt;= 0（不在同步列表里的一次性同步）时直接视为抢到。
    /// </summary>
    public bool TryAcquireLease(long id, string owner, int holdSeconds)
    {
        if (id <= 0) return true;

        var now = DateTime.UtcNow;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE images
               SET lock_owner = $owner, lock_at = $now
             WHERE id = $id
               AND (lock_owner IS NULL OR lock_at IS NULL OR lock_owner = $owner OR lock_at <= $expire)
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", Clock.Format(now));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expire", Clock.Format(now.AddSeconds(-Math.Max(1, holdSeconds))));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>释放租约（只释放属于自己的那一把，避免误清别人的锁）。</summary>
    public void ReleaseLease(long id, string owner)
    {
        if (id <= 0) return;

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET lock_owner = NULL, lock_at = NULL WHERE id = $id AND lock_owner = $owner";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
    }

    /// <summary>清理所有超时的同步租约，返回清理条数（进程启动时兜底，避免异常退出后长时间无法重新下载）。</summary>
    public int ClearExpiredLeases(int holdSeconds)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET lock_owner = NULL, lock_at = NULL WHERE lock_at IS NOT NULL AND lock_at <= $expire";
        command.Parameters.AddWithValue("$expire", Clock.Format(DateTime.UtcNow.AddSeconds(-Math.Max(1, holdSeconds))));
        return command.ExecuteNonQuery();
    }

    /// <summary>筛选出已到检查时间的任务。</summary>
    public List<ImageTask> GetDue(DateTime utcNow)
    {
        var due = new List<ImageTask>();
        foreach (var task in GetAll())
        {
            if (!task.Enabled) continue;
            var last = Clock.Parse(task.LastCheckedAt);
            if (last is null)
            {
                due.Add(task);
                continue;
            }
            var interval = TimeSpan.FromSeconds(Math.Max(1, task.IntervalSeconds));
            if (utcNow - last.Value >= interval) due.Add(task);
        }
        return due;
    }

    public int Count()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM images";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ImageTask Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Image = reader.GetString(1),
        Repository = reader.GetString(2),
        Tag = reader.GetString(3),
        IntervalSeconds = reader.IsDBNull(4) ? Duration.DefaultSeconds : reader.GetInt32(4),
        Enabled = reader.GetInt32(5) != 0,
        CreatedAt = reader.GetString(6),
        UpdatedAt = reader.GetString(7),
        LastCheckedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
        LastSuccessAt = reader.IsDBNull(9) ? null : reader.GetString(9),
        LastDigest = reader.IsDBNull(10) ? null : reader.GetString(10),
        LastStatus = reader.IsDBNull(11) ? null : reader.GetString(11),
        LastError = reader.IsDBNull(12) ? null : reader.GetString(12),
        LastFile = reader.IsDBNull(13) ? null : reader.GetString(13),
        Output = reader.IsDBNull(14) ? null : reader.GetString(14),
    };
}

using Microsoft.Data.Sqlite;

namespace Byxcr.Persistence;

/// <summary>SQLite 数据库连接与结构初始化。</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string FilePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        foreach (var pragma in (string[])
                 [
                     "PRAGMA journal_mode=WAL;",
                     "PRAGMA synchronous=NORMAL;",
                     "PRAGMA busy_timeout=10000;",
                     "PRAGMA foreign_keys=ON;",
                 ])
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        Migrate(connection);
        Upgrade(connection);
    }

    /// <summary>把早期版本的 sync_records.tool 列重命名为 channel（下载通道）。</summary>
    private static void Migrate(SqliteConnection connection)
    {
        if (ColumnExists(connection, "sync_records", "channel")) return;
        if (!ColumnExists(connection, "sync_records", "tool")) return;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE sync_records RENAME COLUMN tool TO channel";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 旧版 SQLite 不支持 RENAME COLUMN：保留旧列名，后续写入由 channel 承接失败则忽略
        }
    }

    /// <summary>旧库结构升级：补新增列、把 interval 的单位从「分钟」迁到「秒」、删掉废弃列。</summary>
    private static void Upgrade(SqliteConnection connection)
    {
        EnsureColumn(connection, "images", "lock_owner", "TEXT");
        EnsureColumn(connection, "images", "lock_at", "TEXT");
        EnsureColumn(connection, "images", "output", "TEXT");

        // interval 单位从「分钟」改成「秒」：老库补出新列，把旧值 ×60 搬过去，再丢掉旧列。
        EnsureColumn(connection, "images", "interval_seconds", "INTEGER");
        if (ColumnExists(connection, "images", "interval_minutes"))
        {
            Execute(connection, "UPDATE images SET interval_seconds = interval_minutes * 60 WHERE interval_seconds IS NULL");
            DropColumn(connection, "images", "interval_minutes");
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        if (ColumnExists(connection, table, column)) return;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 另一个进程可能刚好也在加列，忽略即可
        }
    }

    /// <summary>执行一条无参 SQL；失败静默（结构迁移是尽力而为）。</summary>
    private static void Execute(SqliteConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>尽力删掉已废弃的列；旧版 SQLite 不支持 DROP COLUMN 时保留该列（不影响新代码读写）。</summary>
    private static void DropColumn(SqliteConnection connection, string table, string column)
    {
        if (!ColumnExists(connection, table, column)) return;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE {table} DROP COLUMN {column}";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS images (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            image            TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            repository       TEXT    NOT NULL,
            tag              TEXT    NOT NULL,
            interval_seconds INTEGER NOT NULL DEFAULT 86400,
            enabled          INTEGER NOT NULL DEFAULT 1,
            created_at       TEXT    NOT NULL,
            updated_at       TEXT    NOT NULL,
            last_checked_at  TEXT,
            last_success_at  TEXT,
            last_digest      TEXT,
            last_status      TEXT,
            last_error       TEXT,
            last_file        TEXT,
            output           TEXT,
            lock_owner       TEXT,
            lock_at          TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_images_enabled ON images(enabled);

        CREATE TABLE IF NOT EXISTS sync_records (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            image_id    INTEGER NOT NULL,
            image       TEXT    NOT NULL,
            status      TEXT    NOT NULL,
            channel     TEXT,
            registry    TEXT,
            digest      TEXT,
            file_path   TEXT,
            file_size   INTEGER,
            duration_ms INTEGER,
            message     TEXT,
            trigger_by  TEXT,
            started_at  TEXT    NOT NULL,
            finished_at TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_records_image ON sync_records(image_id, id DESC);
        CREATE INDEX IF NOT EXISTS idx_records_started ON sync_records(started_at DESC);
        """;
}

using Microsoft.Data.Sqlite;

namespace TypeWhisper.Core.Data;

public sealed class TypeWhisperDatabase : ITypeWhisperDatabase
{
    private const int CurrentSchemaVersion = 6;

    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _disposed;

    public TypeWhisperDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _connectionString = $"Data Source={databasePath}";
    }

    public SqliteConnection GetConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
        }

        if (_connection.State != System.Data.ConnectionState.Open)
            _connection.Open();

        return _connection;
    }

    public void Initialize()
    {
        var connection = GetConnection();

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY
            )
        """);

        var version = GetSchemaVersion(connection);

        if (version < 1)
            MigrateToVersion1(connection);
        if (version < 2)
            MigrateToVersion2(connection);
        if (version < 3)
            MigrateToVersion3(connection);
        if (version < 4)
            MigrateToVersion4(connection);
        if (version < 5)
            MigrateToVersion5(connection);
        if (version < 6)
            MigrateToVersion6(connection);
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetSchemaVersion(SqliteConnection connection, int version)
    {
        Exec(connection, $"INSERT OR REPLACE INTO schema_version (version) VALUES ({version})");
    }

    private static void MigrateToVersion1(SqliteConnection connection)
    {
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS transcription_history (
                id TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                final_text TEXT NOT NULL,
                app_name TEXT,
                app_process_name TEXT,
                app_url TEXT,
                duration_seconds REAL NOT NULL DEFAULT 0,
                language TEXT,
                engine_used TEXT NOT NULL DEFAULT 'whisper',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """);

        Exec(connection, """
            CREATE INDEX IF NOT EXISTS idx_history_timestamp
            ON transcription_history(timestamp DESC)
        """);

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS dictionary_entries (
                id TEXT PRIMARY KEY,
                entry_type TEXT NOT NULL,
                original TEXT NOT NULL,
                replacement TEXT,
                case_sensitive INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                usage_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """);

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS snippets (
                id TEXT PRIMARY KEY,
                trigger TEXT NOT NULL,
                replacement TEXT NOT NULL,
                case_sensitive INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                usage_count INTEGER NOT NULL DEFAULT 0,
                tags TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """);

        Exec(connection, """
            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                priority INTEGER NOT NULL DEFAULT 0,
                process_names TEXT NOT NULL DEFAULT '[]',
                url_patterns TEXT NOT NULL DEFAULT '[]',
                input_language TEXT,
                translation_target TEXT,
                selected_task TEXT,
                whisper_mode_override INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """);

        SetSchemaVersion(connection, 1);
    }

    private static void MigrateToVersion2(SqliteConnection connection)
    {
        Exec(connection, "ALTER TABLE transcription_history ADD COLUMN profile_name TEXT");
        SetSchemaVersion(connection, 2);
    }

    private static void MigrateToVersion3(SqliteConnection connection)
    {
        // Only add column if it doesn't exist (fresh DBs already have it from V1)
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(snippets)";
        using var reader = cmd.ExecuteReader();
        var hasTagsColumn = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "tags")
            {
                hasTagsColumn = true;
                break;
            }
        }
        reader.Close();

        if (!hasTagsColumn)
            Exec(connection, "ALTER TABLE snippets ADD COLUMN tags TEXT NOT NULL DEFAULT ''");

        SetSchemaVersion(connection, 3);
    }

    private static void MigrateToVersion4(SqliteConnection connection)
    {
        // Only add column if it doesn't exist (fresh DBs have it from V1 schema update)
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(profiles)";
        using var reader = cmd.ExecuteReader();
        var hasColumn = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "transcription_model_override")
            {
                hasColumn = true;
                break;
            }
        }
        reader.Close();

        if (!hasColumn)
            Exec(connection, "ALTER TABLE profiles ADD COLUMN transcription_model_override TEXT");

        SetSchemaVersion(connection, 4);
    }

    private static void MigrateToVersion5(SqliteConnection connection)
    {
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS prompt_actions (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                system_prompt TEXT NOT NULL,
                icon TEXT NOT NULL DEFAULT '✨',
                is_preset INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0,
                provider_override TEXT,
                model_override TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """);

        // Add prompt_action_id to profiles
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(profiles)";
        using var reader = cmd.ExecuteReader();
        var hasColumn = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "prompt_action_id")
            {
                hasColumn = true;
                break;
            }
        }
        reader.Close();

        if (!hasColumn)
            Exec(connection, "ALTER TABLE profiles ADD COLUMN prompt_action_id TEXT");

        SetSchemaVersion(connection, 5);
    }

    private static void MigrateToVersion6(SqliteConnection connection)
    {
        // Add target_action_plugin_id and hotkey_key to prompt_actions
        AddColumnIfMissing(connection, "prompt_actions", "target_action_plugin_id", "TEXT");
        AddColumnIfMissing(connection, "prompt_actions", "hotkey_key", "TEXT");

        // Add model_used to transcription_history
        AddColumnIfMissing(connection, "transcription_history", "model_used", "TEXT");

        // Add hotkey_data to profiles
        AddColumnIfMissing(connection, "profiles", "hotkey_data", "TEXT");

        SetSchemaVersion(connection, 6);
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string type)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
                return;
        }
        reader.Close();
        Exec(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Close();
            _connection?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VspcAutotaskPlugin.Infrastructure;

/// <summary>
/// Thin SQLite access layer. All plugin state (settings, mappings, ticket links,
/// billing history, activity log) lives in a single local database file.
/// </summary>
public sealed class Db
{
    private readonly string _connectionString;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public Db(AppPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30
        }.ToString();
        Initialize();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS company_mappings (
                vspc_company_uid  TEXT PRIMARY KEY,
                vspc_company_name TEXT NOT NULL,
                at_company_id     INTEGER NOT NULL,
                at_company_name   TEXT NOT NULL,
                auto              INTEGER NOT NULL DEFAULT 0,
                mapped_at         TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS service_mappings (
                service_key     TEXT PRIMARY KEY,
                mode            TEXT NOT NULL,
                at_service_id   INTEGER,
                at_service_name TEXT,
                unit_price      REAL,
                billing_code_id INTEGER,
                period_type     INTEGER,
                updated_at      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS company_billing (
                vspc_company_uid TEXT PRIMARY KEY,
                at_contract_id   INTEGER NOT NULL,
                at_contract_name TEXT,
                enabled_services TEXT NOT NULL DEFAULT '[]',
                updated_at       TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS alarm_rules (
                alarm_template_uid TEXT PRIMARY KEY,
                enabled            INTEGER NOT NULL DEFAULT 0,
                name               TEXT,
                category           TEXT,
                internal_id        INTEGER,
                updated_at         TEXT
            );
            CREATE TABLE IF NOT EXISTS ticket_links (
                active_alarm_uid     TEXT PRIMARY KEY,
                vspc_company_uid     TEXT,
                vspc_company_name    TEXT,
                alarm_template_uid   TEXT,
                alarm_name           TEXT,
                object_name          TEXT,
                at_company_id        INTEGER,
                at_ticket_id         INTEGER,
                at_ticket_number     TEXT,
                state                TEXT NOT NULL,
                last_status          TEXT,
                last_activation_time TEXT,
                repeat_count         INTEGER NOT NULL DEFAULT 0,
                due_at               TEXT,
                last_error           TEXT,
                created_at           TEXT NOT NULL,
                updated_at           TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS billing_adjustments (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                ts                TEXT NOT NULL,
                vspc_company_uid  TEXT NOT NULL,
                vspc_company_name TEXT,
                service_key       TEXT NOT NULL,
                at_contract_id    INTEGER,
                at_service_id     INTEGER,
                prev_units        INTEGER,
                new_units         INTEGER,
                delta             INTEGER,
                effective_date    TEXT,
                dry_run           INTEGER NOT NULL DEFAULT 0,
                result            TEXT
            );
            CREATE TABLE IF NOT EXISTS activity_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                ts      TEXT NOT NULL,
                level   TEXT NOT NULL,
                area    TEXT NOT NULL,
                message TEXT NOT NULL,
                detail  TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_ticket_links_state ON ticket_links (state);
            CREATE INDEX IF NOT EXISTS ix_billing_adjustments_ts ON billing_adjustments (ts);
            """;
        cmd.ExecuteNonQuery();
    }

    public int Exec(string sql, Dictionary<string, object?>? args = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddArgs(cmd, args);
        return cmd.ExecuteNonQuery();
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, Dictionary<string, object?>? args = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddArgs(cmd, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(map(reader));
        return result;
    }

    public object? Scalar(string sql, Dictionary<string, object?>? args = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddArgs(cmd, args);
        return cmd.ExecuteScalar();
    }

    private static void AddArgs(SqliteCommand cmd, Dictionary<string, object?>? args)
    {
        if (args == null) return;
        foreach (var (key, value) in args)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
    }

    // ---- key/value settings -------------------------------------------------

    public string? GetSetting(string key) =>
        Scalar("SELECT value FROM settings WHERE key = $key", new() { ["$key"] = key }) as string;

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings (key, value) VALUES ($key, $value) " +
             "ON CONFLICT(key) DO UPDATE SET value = $value",
             new() { ["$key"] = key, ["$value"] = value });

    public void DeleteSetting(string key) =>
        Exec("DELETE FROM settings WHERE key = $key", new() { ["$key"] = key });

    public T? GetJson<T>(string key) where T : class
    {
        var raw = GetSetting(key);
        return raw == null ? null : JsonSerializer.Deserialize<T>(raw, Json);
    }

    public void SetJson<T>(string key, T value) =>
        SetSetting(key, JsonSerializer.Serialize(value, Json));

    // ---- sync state (same shape, separate table to keep settings clean) ----

    public string? GetSyncState(string key) =>
        Scalar("SELECT value FROM sync_state WHERE key = $key", new() { ["$key"] = key }) as string;

    public void SetSyncState(string key, string value) =>
        Exec("INSERT INTO sync_state (key, value) VALUES ($key, $value) " +
             "ON CONFLICT(key) DO UPDATE SET value = $value",
             new() { ["$key"] = key, ["$value"] = value });

    public T? GetSyncJson<T>(string key) where T : class
    {
        var raw = GetSyncState(key);
        return raw == null ? null : JsonSerializer.Deserialize<T>(raw, Json);
    }

    public void SetSyncJson<T>(string key, T value) =>
        SetSyncState(key, JsonSerializer.Serialize(value, Json));
}

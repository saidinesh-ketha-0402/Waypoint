using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Storage;

public static class WaypointDb
{
  public const int SchemaVersion = 1;

  public static SqliteConnection Open(string dbPath)
  {
    if (string.IsNullOrWhiteSpace(dbPath))
    {
      throw new ArgumentException("DB path is required.", nameof(dbPath));
    }

    var fullPath = Path.GetFullPath(dbPath);
    var directory = Path.GetDirectoryName(fullPath)
      ?? throw new InvalidOperationException("DB path directory is missing.");
    Directory.CreateDirectory(directory);

    var connection = new SqliteConnection($"Data Source={fullPath};Mode=ReadWriteCreate;Cache=Shared");
    connection.Open();

    Initialize(connection);
    return connection;
  }

  private static void Initialize(SqliteConnection connection)
  {
    ApplyPragmas(connection);
    if (HasCurrentSchema(connection))
    {
      return;
    }

    BootstrapSchema(connection);
  }

  private static void ApplyPragmas(SqliteConnection connection)
  {
    using var pragmaCommand = connection.CreateCommand();
    pragmaCommand.CommandText = """
      PRAGMA journal_mode = WAL;
      PRAGMA synchronous = NORMAL;
      PRAGMA foreign_keys = ON;
      PRAGMA busy_timeout = 2000;
      """;
    pragmaCommand.ExecuteNonQuery();
  }

  private static bool HasCurrentSchema(SqliteConnection connection)
  {
    using var metaExistsCommand = connection.CreateCommand();
    metaExistsCommand.CommandText = """
      SELECT 1
      FROM sqlite_master
      WHERE type = 'table'
        AND name = 'meta'
      LIMIT 1;
      """;
    var hasMetaTable = metaExistsCommand.ExecuteScalar() is not null;
    if (!hasMetaTable)
    {
      return false;
    }

    using var versionCommand = connection.CreateCommand();
    versionCommand.CommandText = "SELECT value FROM meta WHERE key = 'schema_version' LIMIT 1;";
    var versionValue = versionCommand.ExecuteScalar() as string;
    return string.Equals(versionValue, SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
  }

  private static void BootstrapSchema(SqliteConnection connection)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      CREATE TABLE IF NOT EXISTS meta(
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS node(
        id INTEGER PRIMARY KEY,
        kind TEXT NOT NULL,
        name TEXT NOT NULL,
        qualified_name TEXT NOT NULL UNIQUE,
        project TEXT,
        lang TEXT,
        meta_json TEXT,
        content_hash TEXT,
        updated_at TEXT NOT NULL
      );
      CREATE INDEX IF NOT EXISTS ix_node_name ON node(name);
      CREATE INDEX IF NOT EXISTS ix_node_kind ON node(kind);

      CREATE TABLE IF NOT EXISTS span(
        node_id INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
        file_path TEXT NOT NULL,
        start_line INTEGER NOT NULL,
        end_line INTEGER NOT NULL
      );
      CREATE INDEX IF NOT EXISTS ix_span_file ON span(file_path, start_line, end_line);

      CREATE TABLE IF NOT EXISTS edge(
        src INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
        dst INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
        kind TEXT NOT NULL,
        weight REAL NOT NULL DEFAULT 1.0,
        stale INTEGER NOT NULL DEFAULT 0,
        origin_file TEXT,
        created_at TEXT NOT NULL,
        PRIMARY KEY(src, dst, kind)
      );
      CREATE INDEX IF NOT EXISTS ix_edge_dst ON edge(dst, kind);

      CREATE TABLE IF NOT EXISTS task(
        id INTEGER PRIMARY KEY,
        title TEXT NOT NULL,
        goal_text TEXT,
        repo_root TEXT NOT NULL,
        branch TEXT,
        status TEXT NOT NULL CHECK(status IN ('active','paused','done')),
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS session(
        id TEXT PRIMARY KEY,
        task_id INTEGER REFERENCES task(id),
        model TEXT,
        started_at TEXT NOT NULL,
        ended_at TEXT,
        end_reason TEXT,
        input_tokens INTEGER,
        output_tokens INTEGER,
        cache_read_tokens INTEGER,
        brief_injected_via TEXT
      );

      CREATE TABLE IF NOT EXISTS episode(
        id INTEGER PRIMARY KEY,
        session_id TEXT NOT NULL REFERENCES session(id),
        kind TEXT NOT NULL,
        ts TEXT NOT NULL,
        payload_json TEXT,
        summary TEXT
      );
      CREATE INDEX IF NOT EXISTS ix_episode_session ON episode(session_id, ts);

      CREATE TABLE IF NOT EXISTS episode_touch(
        episode_id INTEGER NOT NULL REFERENCES episode(id) ON DELETE CASCADE,
        node_id INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
        how TEXT NOT NULL,
        PRIMARY KEY(episode_id, node_id, how)
      );
      CREATE INDEX IF NOT EXISTS ix_touch_node ON episode_touch(node_id);

      CREATE TABLE IF NOT EXISTS claim(
        id INTEGER PRIMARY KEY,
        task_id INTEGER NOT NULL REFERENCES task(id),
        session_id TEXT REFERENCES session(id),
        kind TEXT NOT NULL CHECK(kind IN ('decision','constraint','attempt_failed','open_item','fact')),
        text TEXT NOT NULL,
        about_node_id INTEGER REFERENCES node(id) ON DELETE SET NULL,
        status TEXT NOT NULL DEFAULT 'current' CHECK(status IN ('current','superseded','resolved')),
        confidence REAL NOT NULL,
        source TEXT NOT NULL,
        derived_from_episode_id INTEGER REFERENCES episode(id),
        superseded_by INTEGER REFERENCES claim(id),
        created_at TEXT NOT NULL,
        invalidated_at TEXT
      );
      CREATE INDEX IF NOT EXISTS ix_claim_task ON claim(task_id, status, kind);

      CREATE VIRTUAL TABLE IF NOT EXISTS episode_fts USING fts5(summary, body, tokenize='porter unicode61');
      CREATE VIRTUAL TABLE IF NOT EXISTS claim_fts USING fts5(text, tokenize='porter unicode61');

      INSERT INTO meta(key, value)
      VALUES('schema_version', '1')
      ON CONFLICT(key) DO UPDATE SET value = excluded.value;
      """;

    command.ExecuteNonQuery();
  }
}

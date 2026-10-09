using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Storage;

public sealed class TraceStore(SqliteConnection connection, TimeProvider? timeProvider = null)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  public long EnsureTaskForPrompt(string repoRoot, string? branch, string prompt)
  {
    var now = ToUtcIso(_timeProvider.GetUtcNow());
    var activeTask = FindActiveTask(repoRoot, branch);

    if (TryParseNewTask(prompt, out var title, out var goalText))
    {
      if (activeTask is not null)
      {
        using var pauseCommand = _connection.CreateCommand();
        pauseCommand.CommandText = """
          UPDATE task
          SET status = 'paused',
              updated_at = $updated_at
          WHERE id = $id;
          """;
        pauseCommand.Parameters.AddWithValue("$id", activeTask.Value.Id);
        pauseCommand.Parameters.AddWithValue("$updated_at", now);
        pauseCommand.ExecuteNonQuery();
      }

      return CreateTask(
        title,
        goalText,
        repoRoot,
        branch,
        "active",
        now);
    }

    if (activeTask is not null)
    {
      return activeTask.Value.Id;
    }

    var defaultTitle = BuildDefaultTitle(prompt);
    return CreateTask(defaultTitle, prompt, repoRoot, branch, "active", now);
  }

  public void EnsureSession(string sessionId, long? taskId = null, string? model = null)
  {
    var now = ToUtcIso(_timeProvider.GetUtcNow());
    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT INTO session(id, task_id, model, started_at)
      VALUES($id, $task_id, $model, $started_at)
      ON CONFLICT(id) DO NOTHING;
      """;
    command.Parameters.AddWithValue("$id", sessionId);
    command.Parameters.AddWithValue("$task_id", (object?)taskId ?? DBNull.Value);
    command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
    command.Parameters.AddWithValue("$started_at", now);
    command.ExecuteNonQuery();
  }

  public void SetSessionTask(string sessionId, long taskId, string? model = null)
  {
    EnsureSession(sessionId, taskId, model);

    using var command = _connection.CreateCommand();
    command.CommandText = """
      UPDATE session
      SET task_id = $task_id,
          model = COALESCE($model, model)
      WHERE id = $id;
      """;
    command.Parameters.AddWithValue("$id", sessionId);
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
    command.ExecuteNonQuery();
  }

  public void MarkBriefInjected(string sessionId, string injectedVia)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      UPDATE session
      SET brief_injected_via = $brief_injected_via
      WHERE id = $id;
      """;
    command.Parameters.AddWithValue("$id", sessionId);
    command.Parameters.AddWithValue("$brief_injected_via", injectedVia);
    command.ExecuteNonQuery();
  }

  public void EndSession(string sessionId, string? reason)
  {
    EnsureSession(sessionId);

    using var command = _connection.CreateCommand();
    command.CommandText = """
      UPDATE session
      SET ended_at = $ended_at,
          end_reason = $end_reason
      WHERE id = $id;
      """;
    command.Parameters.AddWithValue("$id", sessionId);
    command.Parameters.AddWithValue("$ended_at", ToUtcIso(_timeProvider.GetUtcNow()));
    command.Parameters.AddWithValue("$end_reason", (object?)reason ?? DBNull.Value);
    command.ExecuteNonQuery();
  }

  public long? FindActiveTaskId(string repoRoot, string? branch) => FindActiveTask(repoRoot, branch)?.Id;

  public long AddEpisode(string sessionId, string kind, string? payloadJson = null, string? summary = null, string? body = null)
  {
    EnsureSession(sessionId);

    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT INTO episode(session_id, kind, ts, payload_json, summary)
      VALUES($session_id, $kind, $ts, $payload_json, $summary);
      SELECT last_insert_rowid();
      """;
    command.Parameters.AddWithValue("$session_id", sessionId);
    command.Parameters.AddWithValue("$kind", kind);
    command.Parameters.AddWithValue("$ts", ToUtcIso(_timeProvider.GetUtcNow()));
    command.Parameters.AddWithValue("$payload_json", (object?)TrimTo4Kb(payloadJson) ?? DBNull.Value);
    command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
    var id = command.ExecuteScalar();
    if (id is not long episodeId)
    {
      throw new InvalidOperationException("Unable to insert episode.");
    }

    UpsertEpisodeFts(episodeId, summary, body ?? payloadJson);
    return episodeId;
  }

  public int GetSessionOrdinal(string sessionId)
  {
    using var taskCommand = _connection.CreateCommand();
    taskCommand.CommandText = "SELECT task_id, started_at FROM session WHERE id = $id;";
    taskCommand.Parameters.AddWithValue("$id", sessionId);

    using var sessionReader = taskCommand.ExecuteReader();
    if (!sessionReader.Read())
    {
      throw new InvalidOperationException($"Session '{sessionId}' was not found.");
    }

    if (sessionReader.IsDBNull(0))
    {
      return 1;
    }

    var taskId = sessionReader.GetInt64(0);
    var startedAt = sessionReader.GetString(1);

    using var ordinalCommand = _connection.CreateCommand();
    ordinalCommand.CommandText = """
      SELECT COUNT(*)
      FROM session s
      WHERE s.task_id = $task_id
        AND s.started_at <= $started_at
        AND EXISTS (
          SELECT 1
          FROM episode e
          WHERE e.session_id = s.id
        );
      """;
    ordinalCommand.Parameters.AddWithValue("$task_id", taskId);
    ordinalCommand.Parameters.AddWithValue("$started_at", startedAt);

    var ordinal = ordinalCommand.ExecuteScalar();
    return ordinal is long value ? (int)value : 1;
  }

  public string? GetTaskStatus(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT status FROM task WHERE id = $id;";
    command.Parameters.AddWithValue("$id", taskId);
    return command.ExecuteScalar() as string;
  }

  private long CreateTask(
    string title,
    string goalText,
    string repoRoot,
    string? branch,
    string status,
    string now)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT INTO task(title, goal_text, repo_root, branch, status, created_at, updated_at)
      VALUES($title, $goal_text, $repo_root, $branch, $status, $created_at, $updated_at);
      SELECT last_insert_rowid();
      """;
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$goal_text", goalText);
    command.Parameters.AddWithValue("$repo_root", repoRoot);
    command.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
    command.Parameters.AddWithValue("$status", status);
    command.Parameters.AddWithValue("$created_at", now);
    command.Parameters.AddWithValue("$updated_at", now);

    var id = command.ExecuteScalar();
    return id is long taskId
      ? taskId
      : throw new InvalidOperationException("Unable to create task.");
  }

  private (long Id, string Title)? FindActiveTask(string repoRoot, string? branch)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, title
      FROM task
      WHERE repo_root = $repo_root
        AND (branch = $branch OR (branch IS NULL AND $branch IS NULL))
        AND status = 'active'
      ORDER BY created_at DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$repo_root", repoRoot);
    command.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);

    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    return (reader.GetInt64(0), reader.GetString(1));
  }

  private static bool TryParseNewTask(string prompt, out string title, out string goalText)
  {
    title = string.Empty;
    goalText = string.Empty;
    if (string.IsNullOrWhiteSpace(prompt))
    {
      return false;
    }

    var trimmed = prompt.Trim();
    if (trimmed.StartsWith("new task:", StringComparison.OrdinalIgnoreCase))
    {
      var payload = trimmed["new task:".Length..].Trim();
      goalText = payload;
      title = BuildTitle(payload);
      return true;
    }

    if (trimmed.StartsWith("#task", StringComparison.OrdinalIgnoreCase))
    {
      var payload = trimmed["#task".Length..].TrimStart(':', '-', ' ');
      goalText = payload;
      title = BuildTitle(payload);
      return true;
    }

    return false;
  }

  private static string BuildDefaultTitle(string prompt)
  {
    var source = string.IsNullOrWhiteSpace(prompt) ? "Task" : prompt.Trim();
    return BuildTitle(source);
  }

  private static string BuildTitle(string text)
  {
    var normalized = text.Trim();
    if (normalized.Length == 0)
    {
      return "Task";
    }

    return normalized.Length <= 80
      ? normalized
      : normalized[..80];
  }

  private static string? TrimTo4Kb(string? value)
  {
    if (value is null)
    {
      return null;
    }

    const int maxChars = 4096;
    return value.Length <= maxChars ? value : value[..maxChars];
  }

  private static string ToUtcIso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

  private void UpsertEpisodeFts(long episodeId, string? summary, string? body)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT OR REPLACE INTO episode_fts(rowid, summary, body)
      VALUES($rowid, $summary, $body);
      """;
    command.Parameters.AddWithValue("$rowid", episodeId);
    command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
    command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
    command.ExecuteNonQuery();
  }
}

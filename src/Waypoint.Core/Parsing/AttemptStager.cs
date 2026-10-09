using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Storage;

namespace Waypoint.Core.Parsing;

public sealed class AttemptStager(SqliteConnection connection, TimeProvider? timeProvider = null)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  public void Stage(string sessionId, long shellEpisodeId, ShellResult shellResult)
  {
    if (string.IsNullOrWhiteSpace(sessionId))
    {
      throw new ArgumentException("Session id is required.", nameof(sessionId));
    }

    ArgumentNullException.ThrowIfNull(shellResult);

    var taskId = ReadTaskId(sessionId);
    if (!taskId.HasValue)
    {
      return;
    }

    var edited = ReadRecentlyEditedNodes(sessionId, shellEpisodeId);
    var symbolsText = BuildSymbolsText(edited);
    var fallbackNodeId = edited.FirstOrDefault()?.NodeId;

    if (!shellResult.Failed && IsDotnetBuildTestOrRun(shellResult.DotnetCommand))
    {
      ResolveCompileClaims(taskId.Value);
      return;
    }

    if (!shellResult.Failed)
    {
      return;
    }

    var spanIndex = new SpanIndex(_connection);
    var aboutNodeId = fallbackNodeId;

    foreach (var error in shellResult.Errors)
    {
      var match = spanIndex.FindEnclosing(error.File, error.Line);
      if (match is not null)
      {
        AddTouch(shellEpisodeId, match.NodeId, "compiled");
        aboutNodeId ??= match.NodeId;
      }
    }

    foreach (var failedTest in shellResult.Tests?.FailedNames ?? [])
    {
      var testNodeId = FindTestNodeByName(failedTest);
      if (testNodeId.HasValue)
      {
        AddTouch(shellEpisodeId, testNodeId.Value, "tested");
      }
    }

    if (IsCompileErrorOnly(shellResult))
    {
      var firstError = shellResult.Errors[0];
      var text = $"{symbolsText} -> {firstError.Code}: {firstError.Message}";
      UpsertAttemptFailedClaim(taskId.Value, sessionId, shellEpisodeId, text, aboutNodeId, 0.6);
      return;
    }

    if ((shellResult.Tests?.Failed ?? 0) > 0)
    {
      foreach (var failedTest in shellResult.Tests!.FailedNames)
      {
        var text = $"{symbolsText} -> {failedTest} failed";
        UpsertAttemptFailedClaim(taskId.Value, sessionId, shellEpisodeId, text, aboutNodeId, 0.8);
      }

      return;
    }

    if (shellResult.Exception is not null || !string.IsNullOrWhiteSpace(shellResult.DiError))
    {
      var failure = shellResult.Exception is not null
        ? $"{shellResult.Exception.Type}: {shellResult.Exception.Message}"
        : shellResult.DiError!;
      var text = $"{symbolsText} -> {failure}";
      UpsertAttemptFailedClaim(taskId.Value, sessionId, shellEpisodeId, text, aboutNodeId, 0.8);
    }
  }

  private static bool IsDotnetBuildTestOrRun(string command) =>
    command is "build" or "test" or "run";

  private static bool IsCompileErrorOnly(ShellResult shellResult) =>
    shellResult.Errors.Count > 0
    && (shellResult.Tests?.Failed ?? 0) == 0
    && shellResult.Exception is null
    && string.IsNullOrWhiteSpace(shellResult.DiError);

  private long? ReadTaskId(string sessionId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT task_id FROM session WHERE id = $id LIMIT 1;";
    command.Parameters.AddWithValue("$id", sessionId);
    var value = command.ExecuteScalar();
    return value is long taskId ? taskId : null;
  }

  private IReadOnlyList<EditedNode> ReadRecentlyEditedNodes(string sessionId, long shellEpisodeId)
  {
    var sinceEpisodeId = ReadLastSuccessfulDotnetEpisodeId(sessionId, shellEpisodeId);
    using var command = _connection.CreateCommand();
    command.CommandText = """
      WITH recent_edits AS (
        SELECT e.id
        FROM episode e
        WHERE e.session_id = $session_id
          AND e.kind = 'edit'
          AND e.id > $since_id
          AND e.id < $shell_episode_id
        ORDER BY e.id DESC
        LIMIT 10
      )
      SELECT
        n.id,
        n.qualified_name,
        n.name,
        MAX(t.episode_id) AS last_touch_id
      FROM recent_edits re
      JOIN episode_touch t ON t.episode_id = re.id
      JOIN node n ON n.id = t.node_id
      WHERE t.how = 'edited'
      GROUP BY n.id, n.qualified_name, n.name
      ORDER BY last_touch_id DESC, n.id DESC;
      """;
    command.Parameters.AddWithValue("$session_id", sessionId);
    command.Parameters.AddWithValue("$since_id", sinceEpisodeId);
    command.Parameters.AddWithValue("$shell_episode_id", shellEpisodeId);

    var nodes = new List<EditedNode>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      nodes.Add(new EditedNode(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2)));
    }

    return nodes;
  }

  private long ReadLastSuccessfulDotnetEpisodeId(string sessionId, long shellEpisodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT COALESCE(MAX(e.id), 0)
      FROM episode e
      WHERE e.session_id = $session_id
        AND e.kind = 'shell'
        AND e.id < $shell_episode_id
        AND json_extract(e.payload_json, '$.shell.dotnet_command') IN ('build', 'test', 'run')
        AND CAST(COALESCE(json_extract(e.payload_json, '$.shell.failed'), 1) AS INTEGER) = 0;
      """;
    command.Parameters.AddWithValue("$session_id", sessionId);
    command.Parameters.AddWithValue("$shell_episode_id", shellEpisodeId);
    var value = command.ExecuteScalar();
    return value is long id ? id : 0;
  }

  private static string BuildSymbolsText(IReadOnlyList<EditedNode> edited)
  {
    if (edited.Count == 0)
    {
      return "recent edits";
    }

    var names = edited
      .Take(3)
      .Select(static node => node.Name)
      .ToArray();

    return string.Join(", ", names);
  }

  private long? FindTestNodeByName(string failedTestName)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM node
      WHERE kind = 'test'
        AND (qualified_name = $qualified_name
             OR qualified_name LIKE $qualified_suffix
             OR name = $name)
      ORDER BY
        CASE
          WHEN qualified_name = $qualified_name THEN 0
          WHEN name = $name THEN 1
          ELSE 2
        END,
        id ASC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$qualified_name", failedTestName);
    command.Parameters.AddWithValue("$qualified_suffix", "%." + failedTestName);
    command.Parameters.AddWithValue("$name", failedTestName.Split('.').Last());
    var value = command.ExecuteScalar();
    return value is long id ? id : null;
  }

  private void AddTouch(long episodeId, long nodeId, string how)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT OR IGNORE INTO episode_touch(episode_id, node_id, how)
      VALUES($episode_id, $node_id, $how);
      """;
    command.Parameters.AddWithValue("$episode_id", episodeId);
    command.Parameters.AddWithValue("$node_id", nodeId);
    command.Parameters.AddWithValue("$how", how);
    command.ExecuteNonQuery();
  }

  private void ResolveCompileClaims(long taskId)
  {
    var now = ToUtcIso(_timeProvider.GetUtcNow());
    using var command = _connection.CreateCommand();
    command.CommandText = """
      UPDATE claim
      SET status = 'resolved',
          invalidated_at = $now
      WHERE task_id = $task_id
        AND kind = 'attempt_failed'
        AND status = 'current'
        AND REGEXP($pattern, text);
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$now", now);
    command.Parameters.AddWithValue("$pattern", "CS[0-9]{4}:");
    RegisterRegexpFunction();
    command.ExecuteNonQuery();
  }

  private void RegisterRegexpFunction()
  {
    _connection.CreateFunction<string, string, bool>(
      "REGEXP",
      static (pattern, input) =>
      {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(input))
        {
          return false;
        }

        return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
      });
  }

  private void UpsertAttemptFailedClaim(
    long taskId,
    string sessionId,
    long episodeId,
    string text,
    long? aboutNodeId,
    double confidence)
  {
    var normalized = NormalizeClaimText(text);
    using var existingCommand = _connection.CreateCommand();
    existingCommand.CommandText = """
      SELECT id, confidence
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'attempt_failed'
        AND status = 'current'
        AND lower(replace(replace(replace(text, '`', ''), ',', ''), '.', '')) = $normalized;
      """;
    existingCommand.Parameters.AddWithValue("$task_id", taskId);
    existingCommand.Parameters.AddWithValue("$normalized", normalized);
    using var existingReader = existingCommand.ExecuteReader();
    if (existingReader.Read())
    {
      var claimId = existingReader.GetInt64(0);
      var existingConfidence = existingReader.GetDouble(1);
      if (confidence > existingConfidence)
      {
        using var updateCommand = _connection.CreateCommand();
        updateCommand.CommandText = """
          UPDATE claim
          SET confidence = $confidence
          WHERE id = $id;
          """;
        updateCommand.Parameters.AddWithValue("$id", claimId);
        updateCommand.Parameters.AddWithValue("$confidence", confidence);
        updateCommand.ExecuteNonQuery();
      }

      return;
    }

    using var insertCommand = _connection.CreateCommand();
    insertCommand.CommandText = """
      INSERT INTO claim(
        task_id, session_id, kind, text, about_node_id, status, confidence, source, derived_from_episode_id, created_at)
      VALUES(
        $task_id, $session_id, 'attempt_failed', $text, $about_node_id, 'current', $confidence, 'tool_output', $episode_id, $created_at);
      """;
    insertCommand.Parameters.AddWithValue("$task_id", taskId);
    insertCommand.Parameters.AddWithValue("$session_id", sessionId);
    insertCommand.Parameters.AddWithValue("$text", text);
    insertCommand.Parameters.AddWithValue("$about_node_id", (object?)aboutNodeId ?? DBNull.Value);
    insertCommand.Parameters.AddWithValue("$confidence", confidence);
    insertCommand.Parameters.AddWithValue("$episode_id", episodeId);
    insertCommand.Parameters.AddWithValue("$created_at", ToUtcIso(_timeProvider.GetUtcNow()));
    insertCommand.ExecuteNonQuery();
  }

  private static string NormalizeClaimText(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return string.Empty;
    }

    var lowered = text.ToLowerInvariant()
      .Replace("`", string.Empty, StringComparison.Ordinal)
      .Replace(",", string.Empty, StringComparison.Ordinal)
      .Replace(".", string.Empty, StringComparison.Ordinal);

    return Regex.Replace(lowered, @"\s+", " ").Trim();
  }

  private static string ToUtcIso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

  private sealed record EditedNode(long NodeId, string QualifiedName, string Name);
}

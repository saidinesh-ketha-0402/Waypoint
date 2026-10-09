using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class WhatChangedQuery(SqliteConnection connection)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public WhatChangedResult Run(long taskId)
  {
    var changes = LoadChangedNodes(taskId);
    var migrations = LoadMigrations();
    return new WhatChangedResult(changes, migrations);
  }

  private IReadOnlyList<ChangedNodeEntry> LoadChangedNodes(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT n.id, n.name, COALESCE(n.project, ''), COUNT(*) AS edit_count
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      INNER JOIN node n ON n.id = et.node_id
      WHERE s.task_id = $task_id
        AND et.how = 'edited'
      GROUP BY n.id, n.name, n.project
      ORDER BY edit_count DESC, n.id DESC
      LIMIT 80;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);

    using var reader = command.ExecuteReader();
    var rows = new List<ChangedNodeEntry>();
    while (reader.Read())
    {
      var nodeId = reader.GetInt64(0);
      var best = LoadBestDecision(taskId, nodeId);
      rows.Add(new ChangedNodeEntry(
        nodeId,
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        best.Text,
        best.SessionId));
    }

    return rows;
  }

  private (string? Text, string? SessionId) LoadBestDecision(long taskId, long nodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT text, session_id
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'decision'
        AND status = 'current'
        AND about_node_id = $node_id
      ORDER BY confidence DESC, id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$node_id", nodeId);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return (null, null);
    }

    return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
  }

  private IReadOnlyList<string> LoadMigrations()
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT name
      FROM node
      WHERE kind = 'migration'
        AND qualified_name LIKE 'mig:%'
      ORDER BY id DESC
      LIMIT 30;
      """;
    using var reader = command.ExecuteReader();

    var values = new List<string>();
    while (reader.Read())
    {
      values.Add(reader.GetString(0));
    }

    return values;
  }
}

public sealed record WhatChangedResult(
  IReadOnlyList<ChangedNodeEntry> Changes,
  IReadOnlyList<string> Migrations);

public sealed record ChangedNodeEntry(
  long NodeId,
  string Name,
  string Project,
  int EditCount,
  string? DecisionText,
  string? DecisionSessionId);

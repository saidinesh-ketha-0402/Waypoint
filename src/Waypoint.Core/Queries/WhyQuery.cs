using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class WhyQuery(SqliteConnection connection)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public IReadOnlyList<WhyEntry> Run(long nodeId)
  {
    var relatedNodeIds = BuildRelatedNodeIds(nodeId);
    if (relatedNodeIds.Count == 0)
    {
      return [];
    }

    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT c.id, c.task_id, c.session_id, c.kind, c.status, c.text, c.confidence, e.summary
      FROM claim c
      LEFT JOIN episode e ON e.id = c.derived_from_episode_id
      WHERE c.about_node_id IN ({JoinSqlIds(relatedNodeIds.Count)})
        AND c.kind IN ('decision','constraint','attempt_failed','open_item')
      ORDER BY c.id DESC
      LIMIT 48;
      """;
    AddIds(command, relatedNodeIds);

    using var reader = command.ExecuteReader();
    var rows = new List<WhyEntry>();
    while (reader.Read())
    {
      rows.Add(new WhyEntry(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetDouble(6),
        reader.IsDBNull(7) ? null : reader.GetString(7)));
    }

    return rows;
  }

  private HashSet<long> BuildRelatedNodeIds(long nodeId)
  {
    var ids = new HashSet<long> { nodeId };
    var kind = LoadNodeKind(nodeId);
    if (kind is null)
    {
      return [];
    }

    if (kind is "class" or "interface" or "record" or "struct" or "enum")
    {
      // CONTAINS is indexed as member -> type.
      ids.UnionWith(LoadConnectedNodeIds(nodeId, edgeSourceIsNode: false));
    }
    else
    {
      ids.UnionWith(LoadConnectedNodeIds(nodeId, edgeSourceIsNode: true));
    }

    return ids;
  }

  private string? LoadNodeKind(long nodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT kind FROM node WHERE id = $id LIMIT 1;";
    command.Parameters.AddWithValue("$id", nodeId);
    return command.ExecuteScalar() as string;
  }

  private IReadOnlyList<long> LoadConnectedNodeIds(long nodeId, bool edgeSourceIsNode)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = edgeSourceIsNode
      ? """
        SELECT DISTINCT e.dst
        FROM edge e
        WHERE e.kind = 'CONTAINS'
          AND e.src = $id;
        """
      : """
        SELECT DISTINCT e.src
        FROM edge e
        WHERE e.kind = 'CONTAINS'
          AND e.dst = $id;
        """;
    command.Parameters.AddWithValue("$id", nodeId);

    using var reader = command.ExecuteReader();
    var values = new List<long>();
    while (reader.Read())
    {
      values.Add(reader.GetInt64(0));
    }

    return values;
  }

  private static string JoinSqlIds(int count) => string.Join(", ", Enumerable.Range(0, count).Select(index => $"$id{index}"));

  private static void AddIds(SqliteCommand command, IEnumerable<long> ids)
  {
    var index = 0;
    foreach (var id in ids)
    {
      command.Parameters.AddWithValue($"$id{index}", id);
      index++;
    }
  }
}

public sealed record WhyEntry(
  long ClaimId,
  long TaskId,
  string? SessionId,
  string Kind,
  string Status,
  string Text,
  double Confidence,
  string? EpisodeSummary);

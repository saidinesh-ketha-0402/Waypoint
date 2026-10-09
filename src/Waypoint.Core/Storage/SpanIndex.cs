using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Storage;

public sealed class SpanIndex(SqliteConnection connection)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public NodeSpanMatch? FindEnclosing(string filePath, int line)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT
        n.id,
        n.kind,
        n.name,
        n.qualified_name,
        s.file_path,
        s.start_line,
        s.end_line
      FROM span s
      INNER JOIN node n ON n.id = s.node_id
      WHERE s.file_path = $file_path
        AND s.start_line <= $line
        AND s.end_line >= $line
      ORDER BY (s.end_line - s.start_line) ASC, s.start_line DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$file_path", NormalizePath(filePath));
    command.Parameters.AddWithValue("$line", line);

    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    return new NodeSpanMatch(
      reader.GetInt64(0),
      reader.GetString(1),
      reader.GetString(2),
      reader.GetString(3),
      reader.GetString(4),
      reader.GetInt32(5),
      reader.GetInt32(6));
  }

  public IReadOnlyList<NodeSpanMatch> NodesInRange(string filePath, int startLine, int endLine)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT
        n.id,
        n.kind,
        n.name,
        n.qualified_name,
        s.file_path,
        s.start_line,
        s.end_line
      FROM span s
      INNER JOIN node n ON n.id = s.node_id
      WHERE s.file_path = $file_path
        AND s.end_line >= $start_line
        AND s.start_line <= $end_line
      ORDER BY s.start_line ASC, s.end_line ASC;
      """;
    command.Parameters.AddWithValue("$file_path", NormalizePath(filePath));
    command.Parameters.AddWithValue("$start_line", startLine);
    command.Parameters.AddWithValue("$end_line", endLine);

    var results = new List<NodeSpanMatch>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      results.Add(new NodeSpanMatch(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6)));
    }

    return results;
  }

  private static string NormalizePath(string path) => path.Replace('\\', '/');
}

public sealed record NodeSpanMatch(
  long NodeId,
  string Kind,
  string Name,
  string QualifiedName,
  string FilePath,
  int StartLine,
  int EndLine);

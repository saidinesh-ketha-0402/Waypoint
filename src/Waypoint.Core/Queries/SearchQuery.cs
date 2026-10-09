using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class SearchQuery(SqliteConnection connection)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public IReadOnlyList<SearchHit> Run(string query, string? kind = null, int limit = 10)
  {
    if (string.IsNullOrWhiteSpace(query))
    {
      return [];
    }

    var safeLimit = Math.Clamp(limit, 1, 50);
    var ftsQuery = BuildSafeFtsQuery(query);

    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT kind, text, session_id, task_id, rank
      FROM (
        SELECT
          'episode' AS kind,
          COALESCE(e.summary, '') || CASE WHEN e.payload_json IS NULL THEN '' ELSE ' — ' || e.payload_json END AS text,
          e.session_id AS session_id,
          s.task_id AS task_id,
          bm25(episode_fts) AS rank
        FROM episode_fts
        INNER JOIN episode e ON e.id = episode_fts.rowid
        LEFT JOIN session s ON s.id = e.session_id
        WHERE episode_fts MATCH $q
        UNION ALL
        SELECT
          c.kind AS kind,
          c.text AS text,
          c.session_id AS session_id,
          c.task_id AS task_id,
          bm25(claim_fts) AS rank
        FROM claim_fts
        INNER JOIN claim c ON c.id = claim_fts.rowid
        WHERE claim_fts MATCH $q
      )
      WHERE ($kind IS NULL OR kind = $kind)
      ORDER BY rank ASC
      LIMIT $limit;
      """;
    command.Parameters.AddWithValue("$q", ftsQuery);
    command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
    command.Parameters.AddWithValue("$limit", safeLimit);

    using var reader = command.ExecuteReader();
    var rows = new List<SearchHit>();
    while (reader.Read())
    {
      rows.Add(new SearchHit(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.GetDouble(4)));
    }

    return rows;
  }

  private static string BuildSafeFtsQuery(string query)
  {
    var terms = query
      .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
      .Select(term => term.Trim().Replace("\"", string.Empty))
      .Where(term => term.Length > 0)
      .Take(12)
      .Select(term => "\"" + term + "\"")
      .ToArray();

    return terms.Length == 0 ? "\"\"" : string.Join(" ", terms);
  }
}

public sealed record SearchHit(
  string Kind,
  string Text,
  string? SessionId,
  long? TaskId,
  double Rank);

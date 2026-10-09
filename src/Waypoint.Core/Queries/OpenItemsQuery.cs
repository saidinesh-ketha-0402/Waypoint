using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class OpenItemsQuery(SqliteConnection connection, TimeProvider? timeProvider = null)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  public OpenItemsResult Run(long taskId, long? resolveId = null)
  {
    string? resolvedMessage = null;
    if (resolveId.HasValue)
    {
      resolvedMessage = ResolveItem(taskId, resolveId.Value);
    }

    var items = LoadOpenItems(taskId);
    return new OpenItemsResult(resolvedMessage, items);
  }

  private string ResolveItem(long taskId, long resolveId)
  {
    string? text;
    using (var read = _connection.CreateCommand())
    {
      read.CommandText = """
        SELECT text
        FROM claim
        WHERE id = $id
          AND task_id = $task_id
          AND kind = 'open_item'
          AND status = 'current'
        LIMIT 1;
        """;
      read.Parameters.AddWithValue("$id", resolveId);
      read.Parameters.AddWithValue("$task_id", taskId);
      text = read.ExecuteScalar() as string;
    }

    if (string.IsNullOrWhiteSpace(text))
    {
      return $"Open item #{resolveId} was not found or is already resolved.";
    }

    using var update = _connection.CreateCommand();
    update.CommandText = """
      UPDATE claim
      SET status = 'resolved',
          invalidated_at = $invalidated_at
      WHERE id = $id;
      """;
    update.Parameters.AddWithValue("$id", resolveId);
    update.Parameters.AddWithValue("$invalidated_at", _timeProvider.GetUtcNow().UtcDateTime.ToString("O"));
    update.ExecuteNonQuery();
    return $"Resolved #{resolveId}: {text}";
  }

  private IReadOnlyList<OpenItemEntry> LoadOpenItems(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, text
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'open_item'
        AND status = 'current'
      ORDER BY confidence DESC, id ASC
      LIMIT 60;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();

    var items = new List<OpenItemEntry>();
    while (reader.Read())
    {
      items.Add(new OpenItemEntry(reader.GetInt64(0), reader.GetString(1)));
    }

    return items;
  }
}

public sealed record OpenItemsResult(
  string? ResolveMessage,
  IReadOnlyList<OpenItemEntry> Items);

public sealed record OpenItemEntry(long Id, string Text);

using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Storage;

public sealed class GraphStore(SqliteConnection connection, TimeProvider? timeProvider = null)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  public long UpsertNode(NodeUpsert node)
  {
    var now = ToUtcIso(_timeProvider.GetUtcNow());

    using var upsertCommand = _connection.CreateCommand();
    upsertCommand.CommandText = """
      INSERT INTO node(kind, name, qualified_name, project, lang, meta_json, content_hash, updated_at)
      VALUES($kind, $name, $qualified_name, $project, $lang, $meta_json, $content_hash, $updated_at)
      ON CONFLICT(qualified_name) DO UPDATE SET
        kind = excluded.kind,
        name = excluded.name,
        project = excluded.project,
        lang = excluded.lang,
        meta_json = excluded.meta_json,
        content_hash = excluded.content_hash,
        updated_at = excluded.updated_at;
      """;
    upsertCommand.Parameters.AddWithValue("$kind", node.Kind);
    upsertCommand.Parameters.AddWithValue("$name", node.Name);
    upsertCommand.Parameters.AddWithValue("$qualified_name", node.QualifiedName);
    upsertCommand.Parameters.AddWithValue("$project", (object?)node.Project ?? DBNull.Value);
    upsertCommand.Parameters.AddWithValue("$lang", (object?)node.Lang ?? DBNull.Value);
    upsertCommand.Parameters.AddWithValue("$meta_json", (object?)node.MetaJson ?? DBNull.Value);
    upsertCommand.Parameters.AddWithValue("$content_hash", (object?)node.ContentHash ?? DBNull.Value);
    upsertCommand.Parameters.AddWithValue("$updated_at", now);
    upsertCommand.ExecuteNonQuery();

    return NodeIdByQualifiedName(node.QualifiedName)
      ?? throw new InvalidOperationException($"Unable to load node id for {node.QualifiedName}.");
  }

  public void ReplaceSpans(long nodeId, IReadOnlyList<SpanUpsert> spans)
  {
    using var transaction = _connection.BeginTransaction();

    using (var deleteCommand = _connection.CreateCommand())
    {
      deleteCommand.Transaction = transaction;
      deleteCommand.CommandText = "DELETE FROM span WHERE node_id = $node_id;";
      deleteCommand.Parameters.AddWithValue("$node_id", nodeId);
      deleteCommand.ExecuteNonQuery();
    }

    foreach (var span in spans)
    {
      InsertSpan(transaction, nodeId, span.FilePath, span.StartLine, span.EndLine);
    }

    transaction.Commit();
  }

  public void UpsertEdge(EdgeUpsert edge)
  {
    var srcId = NodeIdByQualifiedName(edge.SourceQualifiedName)
      ?? -1;
    var dstId = NodeIdByQualifiedName(edge.DestinationQualifiedName)
      ?? -1;
    if (srcId < 0 || dstId < 0)
    {
      // Semantic indexing can surface edges that point outside the current file snapshot.
      // Skip unresolved links instead of failing the full index run.
      return;
    }
    var now = ToUtcIso(_timeProvider.GetUtcNow());

    using var command = _connection.CreateCommand();
    command.CommandText = """
      INSERT INTO edge(src, dst, kind, weight, stale, origin_file, created_at)
      VALUES($src, $dst, $kind, $weight, $stale, $origin_file, $created_at)
      ON CONFLICT(src, dst, kind) DO UPDATE SET
        weight = excluded.weight,
        stale = excluded.stale,
        origin_file = excluded.origin_file,
        created_at = excluded.created_at;
      """;
    command.Parameters.AddWithValue("$src", srcId);
    command.Parameters.AddWithValue("$dst", dstId);
    command.Parameters.AddWithValue("$kind", edge.Kind);
    command.Parameters.AddWithValue("$weight", edge.Weight);
    command.Parameters.AddWithValue("$stale", edge.Stale ? 1 : 0);
    command.Parameters.AddWithValue("$origin_file", (object?)NormalizeNullablePath(edge.OriginFile) ?? DBNull.Value);
    command.Parameters.AddWithValue("$created_at", now);
    command.ExecuteNonQuery();
  }

  public void ReplaceFile(
    string filePath,
    IReadOnlyList<NodeUpsert> nodes,
    IReadOnlyList<SpanUpsert> spans,
    IReadOnlyList<EdgeUpsert> edges)
  {
    var normalizedFilePath = NormalizePath(filePath);

    using var transaction = _connection.BeginTransaction();

    using (var edgeDelete = _connection.CreateCommand())
    {
      edgeDelete.Transaction = transaction;
      edgeDelete.CommandText = "DELETE FROM edge WHERE origin_file = $origin_file;";
      edgeDelete.Parameters.AddWithValue("$origin_file", normalizedFilePath);
      edgeDelete.ExecuteNonQuery();
    }

    using (var spanDelete = _connection.CreateCommand())
    {
      spanDelete.Transaction = transaction;
      spanDelete.CommandText = "DELETE FROM span WHERE file_path = $file_path;";
      spanDelete.Parameters.AddWithValue("$file_path", normalizedFilePath);
      spanDelete.ExecuteNonQuery();
    }

    foreach (var node in nodes)
    {
      UpsertNodeWithTransaction(transaction, node);
    }

    foreach (var span in spans)
    {
      var nodeId = NodeIdByQualifiedName(span.NodeQualifiedName)
        ?? throw new InvalidOperationException($"Unknown node '{span.NodeQualifiedName}'.");
      InsertSpan(transaction, nodeId, span.FilePath, span.StartLine, span.EndLine);
    }

    foreach (var edge in edges)
    {
      var srcId = NodeIdByQualifiedName(edge.SourceQualifiedName)
        ?? -1;
      var dstId = NodeIdByQualifiedName(edge.DestinationQualifiedName)
        ?? -1;
      if (srcId < 0 || dstId < 0)
      {
        continue;
      }
      var now = ToUtcIso(_timeProvider.GetUtcNow());

      using var edgeCommand = _connection.CreateCommand();
      edgeCommand.Transaction = transaction;
      edgeCommand.CommandText = """
        INSERT INTO edge(src, dst, kind, weight, stale, origin_file, created_at)
        VALUES($src, $dst, $kind, $weight, $stale, $origin_file, $created_at)
        ON CONFLICT(src, dst, kind) DO UPDATE SET
          weight = excluded.weight,
          stale = excluded.stale,
          origin_file = excluded.origin_file,
          created_at = excluded.created_at;
        """;
      edgeCommand.Parameters.AddWithValue("$src", srcId);
      edgeCommand.Parameters.AddWithValue("$dst", dstId);
      edgeCommand.Parameters.AddWithValue("$kind", edge.Kind);
      edgeCommand.Parameters.AddWithValue("$weight", edge.Weight);
      edgeCommand.Parameters.AddWithValue("$stale", edge.Stale ? 1 : 0);
      edgeCommand.Parameters.AddWithValue("$origin_file", (object?)NormalizeNullablePath(edge.OriginFile) ?? DBNull.Value);
      edgeCommand.Parameters.AddWithValue("$created_at", now);
      edgeCommand.ExecuteNonQuery();
    }

    transaction.Commit();
  }

  public long? NodeIdByQualifiedName(string qualifiedName)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT id FROM node WHERE qualified_name = $qualified_name;";
    command.Parameters.AddWithValue("$qualified_name", qualifiedName);
    var result = command.ExecuteScalar();

    return result is long id ? id : null;
  }

  private void UpsertNodeWithTransaction(SqliteTransaction transaction, NodeUpsert node)
  {
    var now = ToUtcIso(_timeProvider.GetUtcNow());

    using var command = _connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
      INSERT INTO node(kind, name, qualified_name, project, lang, meta_json, content_hash, updated_at)
      VALUES($kind, $name, $qualified_name, $project, $lang, $meta_json, $content_hash, $updated_at)
      ON CONFLICT(qualified_name) DO UPDATE SET
        kind = excluded.kind,
        name = excluded.name,
        project = excluded.project,
        lang = excluded.lang,
        meta_json = excluded.meta_json,
        content_hash = excluded.content_hash,
        updated_at = excluded.updated_at;
      """;
    command.Parameters.AddWithValue("$kind", node.Kind);
    command.Parameters.AddWithValue("$name", node.Name);
    command.Parameters.AddWithValue("$qualified_name", node.QualifiedName);
    command.Parameters.AddWithValue("$project", (object?)node.Project ?? DBNull.Value);
    command.Parameters.AddWithValue("$lang", (object?)node.Lang ?? DBNull.Value);
    command.Parameters.AddWithValue("$meta_json", (object?)node.MetaJson ?? DBNull.Value);
    command.Parameters.AddWithValue("$content_hash", (object?)node.ContentHash ?? DBNull.Value);
    command.Parameters.AddWithValue("$updated_at", now);
    command.ExecuteNonQuery();
  }

  private void InsertSpan(SqliteTransaction transaction, long nodeId, string filePath, int startLine, int endLine)
  {
    using var insertCommand = _connection.CreateCommand();
    insertCommand.Transaction = transaction;
    insertCommand.CommandText = """
      INSERT INTO span(node_id, file_path, start_line, end_line)
      VALUES($node_id, $file_path, $start_line, $end_line);
      """;
    insertCommand.Parameters.AddWithValue("$node_id", nodeId);
    insertCommand.Parameters.AddWithValue("$file_path", NormalizePath(filePath));
    insertCommand.Parameters.AddWithValue("$start_line", startLine);
    insertCommand.Parameters.AddWithValue("$end_line", endLine);
    insertCommand.ExecuteNonQuery();
  }

  private static string NormalizePath(string path) => path.Replace('\\', '/');
  private static string? NormalizeNullablePath(string? path) => path is null ? null : NormalizePath(path);

  private static string ToUtcIso(DateTimeOffset value) => value.UtcDateTime.ToString("O");
}

public sealed record NodeUpsert(
  string Kind,
  string Name,
  string QualifiedName,
  string? Project = null,
  string? Lang = null,
  string? MetaJson = null,
  string? ContentHash = null);

public sealed record SpanUpsert(
  string NodeQualifiedName,
  string FilePath,
  int StartLine,
  int EndLine);

public sealed record EdgeUpsert(
  string SourceQualifiedName,
  string DestinationQualifiedName,
  string Kind,
  double Weight = 1.0,
  bool Stale = false,
  string? OriginFile = null);

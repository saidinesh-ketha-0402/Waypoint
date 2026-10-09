using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class TaskGraphQuery(SqliteConnection connection)
{
  private static readonly string[] OneHopKinds = ["INJECTS", "REGISTERED_AS", "IMPLEMENTS", "ROUTES_TO", "CONTAINS"];
  private static readonly string[] TypeKinds = ["class", "interface", "record", "struct"];
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public TaskGraphResult Run(long taskId, bool includeUntouchedEndpoints = false, int seedCap = 60, int neighborhoodCap = 40)
  {
    var task = LoadTask(taskId) ?? throw new InvalidOperationException($"Task '{taskId}' was not found.");
    var touchByNode = LoadTouchSummaries(taskId);
    var touchedNodes = LoadTouchedNodes(taskId)
      .Where(node => !ShouldCollapseFileNode(node, touchByNode.Keys))
      .Take(Math.Max(seedCap, 1))
      .ToArray();

    var selected = new HashSet<long>(touchedNodes.Select(node => node.NodeId));
    var neighborScores = ScoreNeighborhoodCandidates(selected);
    foreach (var nodeId in ScoreImpactEndpointCandidates(touchedNodes.Select(node => node.NodeId), taskId))
    {
      neighborScores[nodeId] = neighborScores.TryGetValue(nodeId, out var score) ? score + 2 : 2;
    }

    foreach (var nodeId in RankCandidates(neighborScores, selected, includeUntouchedEndpoints).Take(Math.Max(neighborhoodCap, 0)))
    {
      selected.Add(nodeId);
    }

    var claims = LoadClaims(taskId);
    foreach (var claim in claims)
    {
      if (claim.AboutNodeId.HasValue)
      {
        selected.Add(claim.AboutNodeId.Value);
      }
    }

    var nodes = LoadNodes(selected, touchByNode)
      .OrderBy(node => node.Label, StringComparer.Ordinal)
      .ToArray();
    var edges = LoadGraphEdges(selected);
    var claimEdges = BuildClaimEdges(claims, selected);

    return new TaskGraphResult(task, nodes, claims, edges, claimEdges, DateTimeOffset.UtcNow);
  }

  private TaskGraphTask? LoadTask(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT t.id, t.title, (
        SELECT COUNT(*)
        FROM session s
        WHERE s.task_id = t.id
      ) AS session_count
      FROM task t
      WHERE t.id = $task_id
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    return new TaskGraphTask(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2));
  }

  private Dictionary<long, TouchSummary> LoadTouchSummaries(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT
        et.node_id,
        COUNT(*) AS touch_count,
        SUM(CASE WHEN et.how = 'edited' THEN 1 ELSE 0 END) AS edited_count,
        GROUP_CONCAT(DISTINCT s.id) AS sessions
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      WHERE s.task_id = $task_id
      GROUP BY et.node_id;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();

    var summaries = new Dictionary<long, TouchSummary>();
    while (reader.Read())
    {
      var nodeId = reader.GetInt64(0);
      var sessions = reader.IsDBNull(3)
        ? []
        : reader.GetString(3)
          .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.Ordinal)
          .OrderBy(value => value, StringComparer.Ordinal)
          .ToArray();
      summaries[nodeId] = new TouchSummary(
        reader.GetInt32(1),
        reader.GetInt32(2),
        sessions);
    }

    return summaries;
  }

  private IReadOnlyList<NodeSelectionRow> LoadTouchedNodes(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT
        n.id,
        n.kind,
        n.name,
        n.qualified_name,
        n.project,
        SUM(CASE WHEN et.how = 'edited' THEN 1 ELSE 0 END) AS edited_count,
        COUNT(*) AS touch_count
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      INNER JOIN node n ON n.id = et.node_id
      WHERE s.task_id = $task_id
      GROUP BY n.id
      ORDER BY
        CASE WHEN SUM(CASE WHEN et.how = 'edited' THEN 1 ELSE 0 END) > 0 THEN 1 ELSE 0 END DESC,
        touch_count DESC,
        n.name ASC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);

    using var reader = command.ExecuteReader();
    var rows = new List<NodeSelectionRow>();
    while (reader.Read())
    {
      rows.Add(new NodeSelectionRow(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4)));
    }

    return rows;
  }

  private bool ShouldCollapseFileNode(NodeSelectionRow node, ICollection<long> touchedNodeIds)
  {
    if (!string.Equals(node.Kind, "file", StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    var filePath = TryReadFilePath(node);
    if (string.IsNullOrWhiteSpace(filePath))
    {
      return false;
    }

    var idSlots = string.Join(", ", touchedNodeIds.Select((_, index) => $"$id{index}"));
    if (idSlots.Length == 0)
    {
      return false;
    }

    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT EXISTS(
        SELECT 1
        FROM span sp
        INNER JOIN node n ON n.id = sp.node_id
        WHERE sp.file_path = $file_path
          AND n.id IN ({idSlots})
          AND lower(n.kind) <> 'file'
      );
      """;
    command.Parameters.AddWithValue("$file_path", NormalizePath(filePath!));
    AddIds(command, touchedNodeIds);
    return command.ExecuteScalar() is long found && found == 1;
  }

  private Dictionary<long, int> ScoreNeighborhoodCandidates(HashSet<long> selected)
  {
    var scores = new Dictionary<long, int>();
    if (selected.Count == 0)
    {
      return scores;
    }

    var idSlots = string.Join(", ", selected.Select((_, index) => $"$id{index}"));
    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT e.src, e.dst, e.kind, dst.kind AS dst_kind
      FROM edge e
      INNER JOIN node dst ON dst.id = e.dst
      WHERE e.kind IN ({JoinKinds(OneHopKinds)})
        AND (e.src IN ({idSlots}) OR e.dst IN ({idSlots}));
      """;
    AddKinds(command, OneHopKinds);
    AddIds(command, selected);
    AddIds(command, selected, selected.Count);
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      var src = reader.GetInt64(0);
      var dst = reader.GetInt64(1);
      var kind = reader.GetString(2);
      var dstKind = reader.GetString(3);
      if (string.Equals(kind, "CONTAINS", StringComparison.Ordinal) && !TypeKinds.Contains(dstKind, StringComparer.Ordinal))
      {
        continue;
      }

      var candidate = selected.Contains(src) ? dst : src;
      if (selected.Contains(candidate))
      {
        continue;
      }

      scores[candidate] = scores.TryGetValue(candidate, out var existing) ? existing + 1 : 1;
    }

    return scores;
  }

  private IEnumerable<long> ScoreImpactEndpointCandidates(IEnumerable<long> touchedNodeIds, long taskId)
  {
    var nodeIds = ExpandTouchedTypeCandidates(touchedNodeIds).ToArray();
    if (nodeIds.Length == 0)
    {
      return [];
    }

    var endpointNodeIds = new HashSet<long>();
    var query = new ImpactQuery(_connection);
    foreach (var nodeId in nodeIds)
    {
      var node = LoadNodeKind(nodeId);
      if (node is null || !TypeKinds.Contains(node, StringComparer.Ordinal))
      {
        continue;
      }

      var impact = query.Run(nodeId, depth: 2, throughDi: true, taskId: taskId);
      foreach (var endpoint in impact.Endpoints)
      {
        endpointNodeIds.Add(endpoint.Endpoint.NodeId);
        endpointNodeIds.Add(endpoint.Action.NodeId);
      }
    }

    return endpointNodeIds;
  }

  private IEnumerable<long> ExpandTouchedTypeCandidates(IEnumerable<long> touchedNodeIds)
  {
    var seed = touchedNodeIds.Distinct().ToArray();
    var expanded = new HashSet<long>(seed);
    foreach (var nodeId in seed)
    {
      var kind = LoadNodeKind(nodeId);
      if (kind is null || TypeKinds.Contains(kind, StringComparer.Ordinal))
      {
        continue;
      }

      using var command = _connection.CreateCommand();
      command.CommandText = """
        SELECT e.dst
        FROM edge e
        INNER JOIN node n ON n.id = e.dst
        WHERE e.kind = 'CONTAINS'
          AND e.src = $node_id
          AND lower(n.kind) IN ('class', 'interface', 'record', 'struct');
        """;
      command.Parameters.AddWithValue("$node_id", nodeId);
      using var reader = command.ExecuteReader();
      while (reader.Read())
      {
        expanded.Add(reader.GetInt64(0));
      }
    }

    return expanded;
  }

  private string? LoadNodeKind(long nodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = "SELECT lower(kind) FROM node WHERE id = $id LIMIT 1;";
    command.Parameters.AddWithValue("$id", nodeId);
    return command.ExecuteScalar() as string;
  }

  private IEnumerable<long> RankCandidates(Dictionary<long, int> scores, HashSet<long> selected, bool includeUntouchedEndpoints)
  {
    _ = includeUntouchedEndpoints;
    if (scores.Count == 0)
    {
      return [];
    }

    var nodeData = LoadNodeInfo(scores.Keys);
    return nodeData
      .OrderByDescending(node => scores[node.NodeId])
      .ThenBy(node => node.Name, StringComparer.Ordinal)
      .Select(node => node.NodeId);
  }

  private IReadOnlyList<NodeSelectionRow> LoadNodeInfo(IEnumerable<long> nodeIds)
  {
    var ids = nodeIds.Distinct().ToArray();
    if (ids.Length == 0)
    {
      return [];
    }

    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT id, kind, name, qualified_name, project
      FROM node
      WHERE id IN ({JoinIds(ids.Length)});
      """;
    AddIds(command, ids);
    using var reader = command.ExecuteReader();

    var rows = new List<NodeSelectionRow>();
    while (reader.Read())
    {
      rows.Add(new NodeSelectionRow(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4)));
    }

    return rows;
  }

  private IReadOnlyList<TaskGraphClaim> LoadClaims(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, kind, status, session_id, confidence, text, about_node_id, superseded_by
      FROM claim
      WHERE task_id = $task_id
      ORDER BY id ASC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();

    var claims = new List<TaskGraphClaim>();
    while (reader.Read())
    {
      claims.Add(new TaskGraphClaim(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetDouble(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7)));
    }

    return claims;
  }

  private IReadOnlyList<TaskGraphNode> LoadNodes(HashSet<long> selected, IReadOnlyDictionary<long, TouchSummary> touchByNode)
  {
    var rows = LoadNodeInfo(selected);
    return rows.Select(row =>
    {
      var summary = touchByNode.TryGetValue(row.NodeId, out var value) ? value : TouchSummary.Empty;
      return new TaskGraphNode(
        row.NodeId,
        row.Kind,
        row.Name,
        BuildShortLabel(row.Kind, row.Name, row.QualifiedName),
        row.QualifiedName,
        row.Project,
        summary.TouchCount > 0,
        summary.EditedCount,
        summary.Sessions);
    }).ToArray();
  }

  private IReadOnlyList<TaskGraphEdge> LoadGraphEdges(HashSet<long> selected)
  {
    if (selected.Count == 0)
    {
      return [];
    }

    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT e.src, e.dst, e.kind, dst.kind AS dst_kind
      FROM edge e
      INNER JOIN node dst ON dst.id = e.dst
      WHERE e.kind IN ({JoinKinds(OneHopKinds)})
        AND e.src IN ({JoinIds(selected.Count)})
        AND e.dst IN ({JoinIds(selected.Count, selected.Count)});
      """;
    AddKinds(command, OneHopKinds);
    AddIds(command, selected);
    AddIds(command, selected, selected.Count);
    using var reader = command.ExecuteReader();

    var edges = new List<TaskGraphEdge>();
    while (reader.Read())
    {
      var kind = reader.GetString(2);
      if (string.Equals(kind, "CONTAINS", StringComparison.Ordinal) && !TypeKinds.Contains(reader.GetString(3), StringComparer.Ordinal))
      {
        continue;
      }

      edges.Add(new TaskGraphEdge(reader.GetInt64(0), reader.GetInt64(1), kind));
    }

    return edges;
  }

  private IReadOnlyList<TaskGraphClaimEdge> BuildClaimEdges(IReadOnlyList<TaskGraphClaim> claims, HashSet<long> selected)
  {
    var edges = new List<TaskGraphClaimEdge>();
    foreach (var claim in claims)
    {
      if (claim.AboutNodeId.HasValue && selected.Contains(claim.AboutNodeId.Value))
      {
        edges.Add(new TaskGraphClaimEdge(claim.ClaimId, $"n{claim.AboutNodeId.Value}", "ABOUT"));
      }

      if (claim.SupersededByClaimId.HasValue)
      {
        edges.Add(new TaskGraphClaimEdge(claim.SupersededByClaimId.Value, $"c{claim.ClaimId}", "SUPERSEDES"));
      }
    }

    return edges;
  }

  private static string BuildShortLabel(string kind, string name, string qualifiedName)
  {
    if (string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase))
    {
      var filePath = TryExtractFilePath(name, qualifiedName);
      if (!string.IsNullOrWhiteSpace(filePath))
      {
        return Path.GetFileName(filePath);
      }
    }

    if (string.Equals(kind, "method", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "property", StringComparison.OrdinalIgnoreCase))
    {
      return BuildMemberLabel(qualifiedName, name);
    }

    return name;
  }

  private static string BuildMemberLabel(string qualifiedName, string fallback)
  {
    var payload = qualifiedName;
    var colon = payload.IndexOf(':');
    if (colon >= 0 && colon < payload.Length - 1)
    {
      payload = payload[(colon + 1)..];
    }

    var paramsStart = payload.IndexOf('(');
    if (paramsStart > 0)
    {
      payload = payload[..paramsStart];
    }

    var segments = payload.Split('.', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length >= 2)
    {
      return $"{segments[^2]}.{segments[^1]}";
    }

    return fallback;
  }

  private static string? TryExtractFilePath(string name, string qualifiedName)
  {
    const string filePrefix = "file:";
    if (!string.IsNullOrWhiteSpace(qualifiedName)
        && qualifiedName.StartsWith(filePrefix, StringComparison.Ordinal))
    {
      return qualifiedName[filePrefix.Length..];
    }

    return name;
  }

  private static string? TryReadFilePath(NodeSelectionRow node)
  {
    const string prefix = "file:";
    if (node.QualifiedName.StartsWith(prefix, StringComparison.Ordinal))
    {
      return node.QualifiedName[prefix.Length..];
    }

    return node.Name;
  }

  private static string NormalizePath(string path) => path.Replace('\\', '/');
  private static string JoinIds(int count, int startIndex = 0) => string.Join(", ", Enumerable.Range(startIndex, count).Select(index => $"$id{index}"));
  private static string JoinKinds(IReadOnlyList<string> kinds) => string.Join(", ", Enumerable.Range(0, kinds.Count).Select(index => $"$kind{index}"));

  private static void AddIds(SqliteCommand command, IEnumerable<long> ids, int startIndex = 0)
  {
    var index = startIndex;
    foreach (var id in ids)
    {
      command.Parameters.AddWithValue($"$id{index}", id);
      index++;
    }
  }

  private static void AddKinds(SqliteCommand command, IReadOnlyList<string> kinds)
  {
    for (var i = 0; i < kinds.Count; i++)
    {
      command.Parameters.AddWithValue($"$kind{i}", kinds[i]);
    }
  }

  private sealed record NodeSelectionRow(long NodeId, string Kind, string Name, string QualifiedName, string? Project);
  private sealed record TouchSummary(int TouchCount, int EditedCount, IReadOnlyList<string> Sessions)
  {
    public static TouchSummary Empty { get; } = new(0, 0, []);
  }
}

public sealed record TaskGraphResult(
  TaskGraphTask Task,
  IReadOnlyList<TaskGraphNode> Nodes,
  IReadOnlyList<TaskGraphClaim> Claims,
  IReadOnlyList<TaskGraphEdge> Edges,
  IReadOnlyList<TaskGraphClaimEdge> ClaimEdges,
  DateTimeOffset GeneratedAtUtc);

public sealed record TaskGraphTask(long TaskId, string Title, int SessionCount);

public sealed record TaskGraphNode(
  long NodeId,
  string Kind,
  string Name,
  string Label,
  string QualifiedName,
  string? Project,
  bool Touched,
  int Edits,
  IReadOnlyList<string> Sessions);

public sealed record TaskGraphClaim(
  long ClaimId,
  string Kind,
  string Status,
  string? SessionId,
  double Confidence,
  string Text,
  long? AboutNodeId,
  long? SupersededByClaimId);

public sealed record TaskGraphEdge(long SourceNodeId, long TargetNodeId, string Kind);

public sealed record TaskGraphClaimEdge(long SourceClaimId, string TargetId, string Kind);

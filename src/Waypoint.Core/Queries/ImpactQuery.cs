using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Waypoint.Core.Queries;

public sealed class ImpactQuery(SqliteConnection connection)
{
  private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

  public ImpactResult Run(long startNodeId, int depth = 2, bool throughDi = true, long? taskId = null)
  {
    var clampedDepth = Math.Clamp(depth, 1, 4);
    var touchedNodeIds = taskId.HasValue ? LoadTouchedNodeIds(taskId.Value) : new HashSet<long>();

    var startNode = LoadNode(startNodeId, touchedNodeIds)
      ?? throw new InvalidOperationException($"Node '{startNodeId}' was not found.");
    var contractIds = BuildContractNodeSet(startNodeId);

    var implementations = LoadImplementations(contractIds, touchedNodeIds);
    var diWalk = throughDi
      ? WalkIncomingInjects(contractIds, clampedDepth, touchedNodeIds)
      : new DiWalkResult(new Dictionary<long, int>(), []);
    var consumers = LoadNodes(diWalk.ConsumerDepthByNode.Keys, touchedNodeIds, diWalk.ConsumerDepthByNode);

    var callGraph = WalkCalls(contractIds, clampedDepth);
    var callers = LoadNodes(callGraph.Callers.Keys, touchedNodeIds, callGraph.Callers);
    var callees = LoadNodes(callGraph.Callees.Keys, touchedNodeIds, callGraph.Callees);

    var endpoints = LoadEndpoints(
      contractIds,
      diWalk.ConsumerDepthByNode.Keys.ToHashSet(),
      callGraph.ReachedNodes,
      touchedNodeIds);

    var reachedNodes = new HashSet<long>(contractIds);
    reachedNodes.UnionWith(diWalk.ConsumerDepthByNode.Keys);
    reachedNodes.UnionWith(callGraph.ReachedNodes);
    var tests = LoadTests(reachedNodes, touchedNodeIds);

    return new ImpactResult(
      startNode,
      implementations,
      consumers,
      diWalk.Links,
      callers,
      callees,
      endpoints,
      tests);
  }

  private HashSet<long> BuildContractNodeSet(long startNodeId)
  {
    var contracts = new HashSet<long> { startNodeId };

    foreach (var interfaceId in LoadImplementedInterfaces(startNodeId))
    {
      contracts.Add(interfaceId);
    }

    foreach (var interfaceMemberId in LoadImplementedInterfaceMembers(startNodeId))
    {
      contracts.Add(interfaceMemberId);
    }

    return contracts;
  }

  private IReadOnlyList<ImplementationImpact> LoadImplementations(HashSet<long> contractIds, HashSet<long> touchedNodeIds)
  {
    var rows = new Dictionary<long, (NodeSummary Node, string? Lifetime, string? SourcePath)>();
    foreach (var contractId in contractIds)
    {
      foreach (var implementationId in LoadImplementationsFromImplements(contractId))
      {
        if (contractIds.Contains(implementationId) || rows.ContainsKey(implementationId))
        {
          continue;
        }

        var implementation = LoadNode(implementationId, touchedNodeIds);
        if (implementation is null)
        {
          continue;
        }

        var registration = LoadRegistration(implementationId, contractId);
        var lifetime = registration?.Lifetime ?? TryReadLifetime(implementation.MetaJson);
        rows[implementationId] = (implementation, lifetime, registration?.OriginFile);
      }

      foreach (var implementationId in LoadImplementationsFromRegistrations(contractId))
      {
        if (contractIds.Contains(implementationId) || rows.ContainsKey(implementationId))
        {
          continue;
        }

        var implementation = LoadNode(implementationId, touchedNodeIds);
        if (implementation is null)
        {
          continue;
        }

        var registration = LoadRegistration(implementationId, contractId);
        var lifetime = registration?.Lifetime ?? TryReadLifetime(implementation.MetaJson);
        rows[implementationId] = (implementation, lifetime, registration?.OriginFile);
      }
    }

    return rows.Values
      .OrderBy(value => value.Node.Name, StringComparer.Ordinal)
      .Select(value => new ImplementationImpact(value.Node, value.Lifetime, value.SourcePath))
      .ToArray();
  }

  private IReadOnlyList<long> LoadImplementationsFromImplements(long contractId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT
        CASE WHEN e.dst = $contract_id THEN e.src ELSE e.dst END AS implementation_id
      FROM edge e
      WHERE e.kind = 'IMPLEMENTS'
        AND (e.dst = $contract_id OR e.src = $contract_id);
      """;
    command.Parameters.AddWithValue("$contract_id", contractId);
    using var reader = command.ExecuteReader();

    var implementationIds = new List<long>();
    while (reader.Read())
    {
      implementationIds.Add(reader.GetInt64(0));
    }

    return implementationIds;
  }

  private IReadOnlyList<long> LoadImplementationsFromRegistrations(long contractId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT DISTINCT e.dst
      FROM edge e
      WHERE e.kind = 'REGISTERED_AS'
        AND e.src = $contract_id;
      """;
    command.Parameters.AddWithValue("$contract_id", contractId);
    using var reader = command.ExecuteReader();

    var implementationIds = new List<long>();
    while (reader.Read())
    {
      implementationIds.Add(reader.GetInt64(0));
    }

    return implementationIds;
  }

  private RegistrationInfo? LoadRegistration(long implementationId, long serviceId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT e.origin_file, service.meta_json, impl.meta_json
      FROM edge e
      INNER JOIN node service ON service.id = e.src
      INNER JOIN node impl ON impl.id = e.dst
      WHERE e.kind = 'REGISTERED_AS'
        AND e.src = $service_id
        AND e.dst = $implementation_id
      ORDER BY e.created_at DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$service_id", serviceId);
    command.Parameters.AddWithValue("$implementation_id", implementationId);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    var originFile = reader.IsDBNull(0) ? null : reader.GetString(0);
    var serviceMeta = reader.IsDBNull(1) ? null : reader.GetString(1);
    var implMeta = reader.IsDBNull(2) ? null : reader.GetString(2);
    var lifetime = TryReadLifetime(serviceMeta) ?? TryReadLifetime(implMeta);
    return new RegistrationInfo(lifetime, originFile);
  }

  private DiWalkResult WalkIncomingInjects(HashSet<long> seedDependencies, int depth, HashSet<long> touchedNodeIds)
  {
    var reached = new Dictionary<long, int>();
    var links = new HashSet<(long ConsumerId, long DependencyId)>();
    var frontier = new HashSet<long>(seedDependencies);
    for (var level = 1; level <= depth && frontier.Count > 0; level++)
    {
      using var command = _connection.CreateCommand();
      command.CommandText = $"""
        SELECT DISTINCT e.src, e.dst
        FROM edge e
        WHERE e.kind = 'INJECTS'
          AND e.stale = 0
          AND e.dst IN ({JoinSqlIds(frontier.Count)});
        """;
      AddIds(command, frontier);
      using var reader = command.ExecuteReader();

      var next = new HashSet<long>();
      while (reader.Read())
      {
        var consumerId = reader.GetInt64(0);
        var dependencyId = reader.GetInt64(1);
        links.Add((consumerId, dependencyId));
        if (seedDependencies.Contains(consumerId))
        {
          continue;
        }

        if (reached.TryAdd(consumerId, level))
        {
          next.Add(consumerId);
        }
      }

      frontier = next;
    }

    var linkRows = links
      .Select(link => new ConsumerLink(
        LoadNode(link.ConsumerId, touchedNodeIds) ?? new NodeSummary(link.ConsumerId, "unknown", link.ConsumerId.ToString(), link.ConsumerId.ToString(), null, null, false),
        LoadNode(link.DependencyId, touchedNodeIds) ?? new NodeSummary(link.DependencyId, "unknown", link.DependencyId.ToString(), link.DependencyId.ToString(), null, null, false)))
      .OrderBy(link => link.Dependency.Name, StringComparer.Ordinal)
      .ThenBy(link => link.Consumer.Name, StringComparer.Ordinal)
      .ToArray();

    return new DiWalkResult(reached, linkRows);
  }

  private CallWalkResult WalkCalls(HashSet<long> startIds, int depth)
  {
    var callers = new Dictionary<long, int>();
    var callees = new Dictionary<long, int>();
    var reached = new HashSet<long>(startIds);

    var incomingFrontier = new HashSet<long>(startIds);
    var outgoingFrontier = new HashSet<long>(startIds);

    for (var level = 1; level <= depth; level++)
    {
      incomingFrontier = WalkSingleCallDirection(incomingFrontier, startIds, callers, reached, incoming: true, level);
      outgoingFrontier = WalkSingleCallDirection(outgoingFrontier, startIds, callees, reached, incoming: false, level);

      if (incomingFrontier.Count == 0 && outgoingFrontier.Count == 0)
      {
        break;
      }
    }

    return new CallWalkResult(callers, callees, reached);
  }

  private HashSet<long> WalkSingleCallDirection(
    HashSet<long> frontier,
    HashSet<long> startIds,
    Dictionary<long, int> bucket,
    HashSet<long> reached,
    bool incoming,
    int level)
  {
    if (frontier.Count == 0)
    {
      return [];
    }

    var sourceColumn = incoming ? "e.src" : "e.dst";
    var targetColumn = incoming ? "e.dst" : "e.src";

    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT DISTINCT {sourceColumn}
      FROM edge e
      WHERE e.kind = 'CALLS'
        AND e.stale = 0
        AND {targetColumn} IN ({JoinSqlIds(frontier.Count)});
      """;
    AddIds(command, frontier);

    using var reader = command.ExecuteReader();
    var next = new HashSet<long>();
    while (reader.Read())
    {
      var nodeId = reader.GetInt64(0);
      reached.Add(nodeId);
      if (startIds.Contains(nodeId))
      {
        continue;
      }

      if (bucket.TryAdd(nodeId, level))
      {
        next.Add(nodeId);
      }
    }

    return next;
  }

  private IReadOnlyList<EndpointImpact> LoadEndpoints(
    HashSet<long> contractIds,
    HashSet<long> consumerIds,
    HashSet<long> reachedNodes,
    HashSet<long> touchedNodeIds)
  {
    var routeRows = LoadRouteRows();
    var endpointById = new Dictionary<long, EndpointImpact>();

    foreach (var route in routeRows)
    {
      if (reachedNodes.Contains(route.Action.NodeId) || contractIds.Contains(route.Action.NodeId))
      {
        endpointById[route.Endpoint.NodeId] = new EndpointImpact(route.Endpoint, route.Action);
      }
    }

    var hasCalls = HasAnyNonStaleCalls();
    if (!hasCalls)
    {
      foreach (var route in routeRows)
      {
        var controllerId = FindContainingControllerId(route.Action.NodeId);
        if (!controllerId.HasValue || !consumerIds.Contains(controllerId.Value))
        {
          continue;
        }

        endpointById[route.Endpoint.NodeId] = new EndpointImpact(route.Endpoint, route.Action);
      }
    }

    return endpointById.Values
      .OrderBy(item => item.Endpoint.Name, StringComparer.Ordinal)
      .Select(item => new EndpointImpact(
        LoadNode(item.Endpoint.NodeId, touchedNodeIds) ?? item.Endpoint,
        LoadNode(item.Action.NodeId, touchedNodeIds) ?? item.Action))
      .ToArray();
  }

  private IReadOnlyList<TestCoverageImpact> LoadTests(HashSet<long> reachedNodes, HashSet<long> touchedNodeIds)
  {
    if (reachedNodes.Count == 0)
    {
      return [];
    }

    using var command = _connection.CreateCommand();
    command.CommandText = $"""
      SELECT DISTINCT n.id
      FROM edge e
      INNER JOIN node n ON n.id = e.src
      WHERE e.kind = 'TESTS'
        AND e.dst IN ({JoinSqlIds(reachedNodes.Count)})
        AND n.kind = 'test';
      """;
    AddIds(command, reachedNodes);

    using var reader = command.ExecuteReader();
    var nodes = new List<TestCoverageImpact>();
    while (reader.Read())
    {
      var testNode = LoadNode(reader.GetInt64(0), touchedNodeIds);
      if (testNode is null)
      {
        continue;
      }

      nodes.Add(new TestCoverageImpact(testNode));
    }

    return nodes
      .OrderBy(item => item.Test.Name, StringComparer.Ordinal)
      .ToArray();
  }

  private IReadOnlyList<RouteRow> LoadRouteRows()
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT ep.id, ep.kind, ep.name, ep.qualified_name, ep.project, ep.meta_json,
             action.id, action.kind, action.name, action.qualified_name, action.project, action.meta_json
      FROM edge e
      INNER JOIN node ep ON ep.id = e.src
      INNER JOIN node action ON action.id = e.dst
      WHERE e.kind = 'ROUTES_TO'
        AND ep.kind = 'endpoint';
      """;

    using var reader = command.ExecuteReader();
    var rows = new List<RouteRow>();
    while (reader.Read())
    {
      rows.Add(new RouteRow(
        ReadNodeSummary(reader, 0, touched: false),
        ReadNodeSummary(reader, 6, touched: false)));
    }

    return rows;
  }

  private bool HasAnyNonStaleCalls()
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT EXISTS(
        SELECT 1
        FROM edge
        WHERE kind = 'CALLS'
          AND stale = 0
      );
      """;
    return command.ExecuteScalar() is long found && found == 1;
  }

  private long? FindContainingControllerId(long actionNodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT controller.id
      FROM edge e
      INNER JOIN node controller
        ON ((e.src = controller.id AND e.dst = $action_id)
            OR (e.dst = controller.id AND e.src = $action_id))
      WHERE e.kind = 'CONTAINS'
        AND controller.kind = 'class'
      ORDER BY controller.id ASC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$action_id", actionNodeId);
    var result = command.ExecuteScalar();
    return result is long id ? id : null;
  }

  private HashSet<long> LoadTouchedNodeIds(long taskId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT DISTINCT et.node_id
      FROM episode_touch et
      INNER JOIN episode e ON e.id = et.episode_id
      INNER JOIN session s ON s.id = e.session_id
      WHERE s.task_id = $task_id;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();

    var ids = new HashSet<long>();
    while (reader.Read())
    {
      ids.Add(reader.GetInt64(0));
    }

    return ids;
  }

  private IReadOnlyList<long> LoadImplementedInterfaces(long nodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT DISTINCT n.id
      FROM edge e
      INNER JOIN node n ON n.id =
        CASE WHEN e.src = $node_id THEN e.dst ELSE e.src END
      WHERE e.kind = 'IMPLEMENTS'
        AND (e.src = $node_id OR e.dst = $node_id)
        AND n.kind = 'interface';
      """;
    command.Parameters.AddWithValue("$node_id", nodeId);

    using var reader = command.ExecuteReader();
    var values = new List<long>();
    while (reader.Read())
    {
      values.Add(reader.GetInt64(0));
    }

    return values;
  }

  private IReadOnlyList<long> LoadImplementedInterfaceMembers(long nodeId)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT DISTINCT n.id
      FROM edge e
      INNER JOIN node n ON n.id = e.dst
      WHERE e.kind = 'IMPLEMENTS'
        AND e.src = $node_id
        AND n.kind IN ('method', 'property');
      """;
    command.Parameters.AddWithValue("$node_id", nodeId);

    using var reader = command.ExecuteReader();
    var values = new List<long>();
    while (reader.Read())
    {
      values.Add(reader.GetInt64(0));
    }

    return values;
  }

  private IReadOnlyList<NodeSummary> LoadNodes(
    IEnumerable<long> nodeIds,
    HashSet<long> touchedNodeIds,
    IReadOnlyDictionary<long, int>? distanceByNode = null)
  {
    var nodes = new List<NodeSummary>();
    foreach (var nodeId in nodeIds.Distinct())
    {
      var node = LoadNode(nodeId, touchedNodeIds);
      if (node is null)
      {
        continue;
      }

      nodes.Add(node);
    }

    return nodes
      .OrderBy(node => distanceByNode is null || !distanceByNode.TryGetValue(node.NodeId, out var distance) ? int.MaxValue : distance)
      .ThenBy(node => node.Name, StringComparer.Ordinal)
      .ToArray();
  }

  private NodeSummary? LoadNode(long nodeId, ISet<long> touchedNodeIds)
  {
    using var command = _connection.CreateCommand();
    command.CommandText = """
      SELECT id, kind, name, qualified_name, project, meta_json
      FROM node
      WHERE id = $id
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$id", nodeId);
    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    return ReadNodeSummary(reader, 0, touchedNodeIds.Contains(nodeId));
  }

  private static NodeSummary ReadNodeSummary(SqliteDataReader reader, int offset, bool touched)
  {
    return new NodeSummary(
      reader.GetInt64(offset),
      reader.GetString(offset + 1),
      reader.GetString(offset + 2),
      reader.GetString(offset + 3),
      reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
      reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
      touched);
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

  private static string? TryReadLifetime(string? metaJson)
  {
    if (string.IsNullOrWhiteSpace(metaJson))
    {
      return null;
    }

    try
    {
      using var doc = JsonDocument.Parse(metaJson);
      if (doc.RootElement.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      return doc.RootElement.TryGetProperty("lifetime", out var lifetime)
        && lifetime.ValueKind == JsonValueKind.String
        ? lifetime.GetString()
        : null;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private sealed record RegistrationInfo(string? Lifetime, string? OriginFile);
  private sealed record RouteRow(NodeSummary Endpoint, NodeSummary Action);
  private sealed record DiWalkResult(
    IReadOnlyDictionary<long, int> ConsumerDepthByNode,
    IReadOnlyList<ConsumerLink> Links);
  private sealed record CallWalkResult(
    IReadOnlyDictionary<long, int> Callers,
    IReadOnlyDictionary<long, int> Callees,
    HashSet<long> ReachedNodes);
}

public sealed record ImpactResult(
  NodeSummary Start,
  IReadOnlyList<ImplementationImpact> Implementations,
  IReadOnlyList<NodeSummary> Consumers,
  IReadOnlyList<ConsumerLink> ConsumerLinks,
  IReadOnlyList<NodeSummary> Callers,
  IReadOnlyList<NodeSummary> Callees,
  IReadOnlyList<EndpointImpact> Endpoints,
  IReadOnlyList<TestCoverageImpact> Tests);

public sealed record NodeSummary(
  long NodeId,
  string Kind,
  string Name,
  string QualifiedName,
  string? Project,
  string? MetaJson,
  bool IsTouched);

public sealed record ImplementationImpact(
  NodeSummary Implementation,
  string? Lifetime,
  string? RegistrationSourcePath);

public sealed record EndpointImpact(
  NodeSummary Endpoint,
  NodeSummary Action);

public sealed record TestCoverageImpact(NodeSummary Test);

public sealed record ConsumerLink(
  NodeSummary Consumer,
  NodeSummary Dependency);

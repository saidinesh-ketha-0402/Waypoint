using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Viz;
using Xunit;

namespace Waypoint.Viz.Tests;

public sealed class SmokeTests
{
  [Fact]
  public void Export_json_contains_touched_method_endpoint_and_supersedes_edge()
  {
    using var fixture = ExportFixture.Create();
    var outputDir = Path.Combine(fixture.RepoRoot, ".git", "waypoint", "viz");
    var exporter = new TaskGraphExporter(fixture.Connection);

    var result = exporter.Export(new ExportRequest(1, outputDir, ExportFormat.Json));
    var json = File.ReadAllText(result.JsonOutputPath);
    using var doc = JsonDocument.Parse(json);

    var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
    Assert.Contains(nodes, node =>
      node.GetProperty("kind").GetString() == "method"
      && node.GetProperty("label").GetString() == "PricingService.Apply"
      && node.GetProperty("touched").GetBoolean()
      && node.GetProperty("edits").GetInt32() == 4);

    Assert.Contains(nodes, node =>
      node.GetProperty("kind").GetString() == "endpoint"
      && node.GetProperty("label").GetString() == "POST /api/basket/checkout");

    var edges = doc.RootElement.GetProperty("edges").EnumerateArray().ToArray();
    Assert.Contains(edges, edge =>
      edge.GetProperty("kind").GetString() == "SUPERSEDES"
      && edge.GetProperty("source").GetString() == "c12"
      && edge.GetProperty("target").GetString() == "c9");
  }

  [Fact]
  public void Export_json_caps_core_nodes_to_100_plus_claims()
  {
    using var fixture = ExportFixture.Create(seedLargeTouchSet: true);
    var outputDir = Path.Combine(fixture.RepoRoot, ".git", "waypoint", "viz");
    var exporter = new TaskGraphExporter(fixture.Connection);

    var result = exporter.Export(new ExportRequest(1, outputDir, ExportFormat.Json));
    using var doc = JsonDocument.Parse(File.ReadAllText(result.JsonOutputPath));
    var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
    var claimCount = nodes.Count(node => node.GetProperty("kind").GetString() == "claim");
    var kgCount = nodes.Length - claimCount;

    Assert.True(kgCount <= 100, $"Expected at most 100 KG nodes but got {kgCount}.");
  }

  [Fact]
  public void Export_html_is_self_contained_and_escapes_embedded_json()
  {
    using var fixture = ExportFixture.Create();
    var outputDir = Path.Combine(fixture.RepoRoot, ".git", "waypoint", "viz");
    var exporter = new TaskGraphExporter(fixture.Connection);

    var result = exporter.Export(new ExportRequest(1, outputDir, ExportFormat.Html));
    var html = File.ReadAllText(result.HtmlOutputPath!);

    Assert.DoesNotContain("src=\"http://", html, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("src=\"https://", html, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("\"nodes\":", html, StringComparison.Ordinal);
    Assert.Contains("\\u003c/script\\u003e\\u003cscript\\u003ealert(1)\\u003c/script\\u003e", html, StringComparison.OrdinalIgnoreCase);
  }

  private sealed class ExportFixture : IDisposable
  {
    public string RepoRoot { get; }
    public SqliteConnection Connection { get; }

    private ExportFixture(string repoRoot, SqliteConnection connection)
    {
      RepoRoot = repoRoot;
      Connection = connection;
    }

    public static ExportFixture Create(bool seedLargeTouchSet = false)
    {
      var repoRoot = CreateTempRepoRoot();
      var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
      var connection = WaypointDb.Open(dbPath);
      SeedBaseGraph(connection, repoRoot);
      if (seedLargeTouchSet)
      {
        SeedLargeTouchSet(connection);
      }

      return new ExportFixture(repoRoot, connection);
    }

    public void Dispose()
    {
      Connection.Dispose();
      if (Directory.Exists(RepoRoot))
      {
        Directory.Delete(RepoRoot, recursive: true);
      }
    }
  }

  private static string CreateTempRepoRoot()
  {
    var root = Path.Combine(Path.GetTempPath(), "waypoint-viz-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, ".git"));
    File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
    return root;
  }

  private static void SeedBaseGraph(SqliteConnection connection, string repoRoot)
  {
    var graph = new GraphStore(connection);
    graph.UpsertNode(new NodeUpsert("class", "PricingService", "T:Shop.Core.Services.PricingService", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("method", "Apply", "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("interface", "IPricingService", "T:Shop.Core.Interfaces.IPricingService", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("class", "BasketService", "T:Shop.Core.Services.BasketService", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("class", "BasketController", "T:Shop.Web.Controllers.BasketController", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("method", "Checkout", "M:Shop.Web.Controllers.BasketController.Checkout()", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("endpoint", "POST /api/basket/checkout", "ep:POST /api/basket/checkout", "Shop.Web"));

    graph.UpsertEdge(new EdgeUpsert("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)", "T:Shop.Core.Services.PricingService", "CONTAINS"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Core.Services.PricingService", "T:Shop.Core.Interfaces.IPricingService", "IMPLEMENTS"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Core.Services.BasketService", "T:Shop.Core.Interfaces.IPricingService", "INJECTS"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Web.Controllers.BasketController", "T:Shop.Core.Services.BasketService", "INJECTS"));
    graph.UpsertEdge(new EdgeUpsert("M:Shop.Web.Controllers.BasketController.Checkout()", "T:Shop.Web.Controllers.BasketController", "CONTAINS"));
    graph.UpsertEdge(new EdgeUpsert("ep:POST /api/basket/checkout", "M:Shop.Web.Controllers.BasketController.Checkout()", "ROUTES_TO"));

    using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO task(id, title, goal_text, repo_root, branch, status, created_at, updated_at)
      VALUES(1, 'Add discount endpoint', 'Add discount endpoint', $repo_root, 'main', 'active', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
      INSERT INTO session(id, task_id, model, started_at) VALUES('s1', 1, 'gpt', '2026-01-01T00:00:00.0000000Z');
      INSERT INTO session(id, task_id, model, started_at) VALUES('s2', 1, 'gpt', '2026-01-01T01:00:00.0000000Z');
      INSERT INTO episode(id, session_id, kind, ts) VALUES(1, 's1', 'edit', '2026-01-01T00:01:00.0000000Z');
      INSERT INTO episode(id, session_id, kind, ts) VALUES(2, 's1', 'edit', '2026-01-01T00:02:00.0000000Z');
      INSERT INTO episode(id, session_id, kind, ts) VALUES(3, 's2', 'edit', '2026-01-01T01:01:00.0000000Z');
      INSERT INTO episode(id, session_id, kind, ts) VALUES(4, 's2', 'edit', '2026-01-01T01:02:00.0000000Z');
      """;
    command.Parameters.AddWithValue("$repo_root", repoRoot);
    command.ExecuteNonQuery();

    var methodId = graph.NodeIdByQualifiedName("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)")!.Value;
    using var touches = connection.CreateCommand();
    touches.CommandText = """
      INSERT INTO episode_touch(episode_id, node_id, how) VALUES(1, $node_id, 'edited');
      INSERT INTO episode_touch(episode_id, node_id, how) VALUES(2, $node_id, 'edited');
      INSERT INTO episode_touch(episode_id, node_id, how) VALUES(3, $node_id, 'edited');
      INSERT INTO episode_touch(episode_id, node_id, how) VALUES(4, $node_id, 'edited');
      """;
    touches.Parameters.AddWithValue("$node_id", methodId);
    touches.ExecuteNonQuery();

    using var claims = connection.CreateCommand();
    claims.CommandText = """
      INSERT INTO claim(id, task_id, session_id, kind, text, about_node_id, status, confidence, source, derived_from_episode_id, created_at)
      VALUES(12, 1, 's2', 'decision', 'Switch to scoped registration. </script><script>alert(1)</script>', $about_node_id, 'current', 0.95, 'regex', 3, '2026-01-01T01:05:00.0000000Z');
      INSERT INTO claim(id, task_id, session_id, kind, text, about_node_id, status, confidence, source, derived_from_episode_id, superseded_by, created_at, invalidated_at)
      VALUES(9, 1, 's1', 'decision', 'Use singleton registration for pricing service.', $about_node_id, 'superseded', 0.8, 'regex', 1, 12, '2026-01-01T00:01:00.0000000Z', '2026-01-01T00:10:00.0000000Z');
      """;
    claims.Parameters.AddWithValue("$about_node_id", methodId);
    claims.ExecuteNonQuery();
  }

  private static void SeedLargeTouchSet(SqliteConnection connection)
  {
    var graph = new GraphStore(connection);
    using var baseEpisode = connection.CreateCommand();
    baseEpisode.CommandText = "INSERT INTO episode(id, session_id, kind, ts) VALUES(900, 's2', 'edit', '2026-01-01T02:00:00.0000000Z');";
    baseEpisode.ExecuteNonQuery();

    for (var i = 0; i < 500; i++)
    {
      var qn = $"M:Shop.Core.Services.Generated{i}.Run()";
      graph.UpsertNode(new NodeUpsert("method", $"Run{i}", qn, "Shop.Core"));
      var nodeId = graph.NodeIdByQualifiedName(qn)!.Value;
      using var touch = connection.CreateCommand();
      touch.CommandText = "INSERT OR IGNORE INTO episode_touch(episode_id, node_id, how) VALUES(900, $node_id, 'edited');";
      touch.Parameters.AddWithValue("$node_id", nodeId);
      touch.ExecuteNonQuery();
    }
  }
}

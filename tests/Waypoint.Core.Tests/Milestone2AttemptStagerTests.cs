using Microsoft.Data.Sqlite;
using Waypoint.Core.Parsing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Core.Tests;

public sealed class Milestone2AttemptStagerTests
{
  [Fact]
  public void Di_failure_after_edit_creates_attempt_failed_claim()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    var trace = new TraceStore(connection);
    var stager = new AttemptStager(connection);

    SeedPricingService(graph);
    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "Fix pricing behavior");
    trace.SetSessionTask("session-1", taskId);

    var editEpisodeId = trace.AddEpisode("session-1", "edit", summary: "edit pricing");
    var pricingMethodId = graph.NodeIdByQualifiedName("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)");
    Assert.NotNull(pricingMethodId);
    AddTouch(connection, editEpisodeId, pricingMethodId!.Value, "edited");

    var shellEpisodeId = trace.AddEpisode("session-1", "shell", """{"shell":{"dotnet_command":"run","failed":true}}""", "dotnet run");
    var result = new DotnetOutputParser(repoRoot).Parse("dotnet run --project src/Shop.Web", ReadFixture("run-di-scoped-singleton.txt"), exitCode: 1);
    Assert.NotNull(result);

    stager.Stage("session-1", shellEpisodeId, result!);

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT text, confidence, source, about_node_id
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'attempt_failed'
      ORDER BY id ASC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Contains("PricingService.Apply", reader.GetString(0));
    Assert.Equal(0.8, reader.GetDouble(1), precision: 3);
    Assert.Equal("tool_output", reader.GetString(2));
    Assert.Equal(pricingMethodId.Value, reader.GetInt64(3));
    Assert.False(reader.Read());
  }

  [Fact]
  public void Compile_failure_claim_auto_resolves_after_successful_build()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    var trace = new TraceStore(connection);
    var stager = new AttemptStager(connection);
    var parser = new DotnetOutputParser(repoRoot);

    SeedPricingService(graph);
    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "Fix compile issues");
    trace.SetSessionTask("session-2", taskId);
    var editEpisodeId = trace.AddEpisode("session-2", "edit", summary: "edit pricing");
    AddTouch(connection, editEpisodeId, graph.NodeIdByQualifiedName("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)")!.Value, "edited");

    var failEpisodeId = trace.AddEpisode("session-2", "shell", """{"shell":{"dotnet_command":"build","failed":true}}""", "dotnet build");
    var failResult = parser.Parse("dotnet build DemoShop.slnx", ReadFixture("build-cs-errors.txt"), exitCode: 1);
    Assert.NotNull(failResult);
    stager.Stage("session-2", failEpisodeId, failResult!);

    var successEpisodeId = trace.AddEpisode("session-2", "shell", """{"shell":{"dotnet_command":"build","failed":false}}""", "dotnet build");
    var successResult = parser.Parse("dotnet build DemoShop.slnx", ReadFixture("build-success.txt"), exitCode: 0);
    Assert.NotNull(successResult);
    stager.Stage("session-2", successEpisodeId, successResult!);

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT status
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'attempt_failed'
      ORDER BY id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    var status = command.ExecuteScalar() as string;
    Assert.Equal("resolved", status);
  }

  [Fact]
  public void Same_failed_test_is_deduped_to_single_claim()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    var trace = new TraceStore(connection);
    var stager = new AttemptStager(connection);
    var parser = new DotnetOutputParser(repoRoot);

    SeedPricingService(graph);
    graph.UpsertNode(new NodeUpsert("test", "Clamps_to_50_percent", "M:Shop.UnitTests.PricingTests.Clamps_to_50_percent()"));
    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "Fix flaky tests");
    trace.SetSessionTask("session-3", taskId);
    var editEpisodeId = trace.AddEpisode("session-3", "edit", summary: "edit pricing");
    AddTouch(connection, editEpisodeId, graph.NodeIdByQualifiedName("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)")!.Value, "edited");

    var firstEpisodeId = trace.AddEpisode("session-3", "shell", """{"shell":{"dotnet_command":"test","failed":true}}""", "dotnet test");
    var firstResult = parser.Parse("dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj", ReadFixture("test-vstest-fail.txt"), exitCode: 1);
    Assert.NotNull(firstResult);
    stager.Stage("session-3", firstEpisodeId, firstResult!);

    var secondEpisodeId = trace.AddEpisode("session-3", "shell", """{"shell":{"dotnet_command":"test","failed":true}}""", "dotnet test");
    var secondResult = parser.Parse("dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj", ReadFixture("test-vstest-fail.txt"), exitCode: 1);
    Assert.NotNull(secondResult);
    stager.Stage("session-3", secondEpisodeId, secondResult!);

    using var claimCount = connection.CreateCommand();
    claimCount.CommandText = """
      SELECT COUNT(*)
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'attempt_failed'
        AND text LIKE '%Clamps_to_50_percent failed';
      """;
    claimCount.Parameters.AddWithValue("$task_id", taskId);
    Assert.Equal(1L, claimCount.ExecuteScalar());
  }

  private static void SeedPricingService(GraphStore graph)
  {
    graph.UpsertNode(new NodeUpsert("class", "PricingService", "T:Shop.Core.Services.PricingService"));
    graph.UpsertNode(new NodeUpsert("method", "PricingService.Apply", "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)"));
    graph.ReplaceFile(
      "src/Shop.Core/Services/PricingService.cs",
      [],
      [
        new SpanUpsert("T:Shop.Core.Services.PricingService", "src/Shop.Core/Services/PricingService.cs", 1, 50),
        new SpanUpsert("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)", "src/Shop.Core/Services/PricingService.cs", 10, 30)
      ],
      []);
  }

  private static void AddTouch(SqliteConnection connection, long episodeId, long nodeId, string how)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT OR IGNORE INTO episode_touch(episode_id, node_id, how)
      VALUES($episode_id, $node_id, $how);
      """;
    command.Parameters.AddWithValue("$episode_id", episodeId);
    command.Parameters.AddWithValue("$node_id", nodeId);
    command.Parameters.AddWithValue("$how", how);
    command.ExecuteNonQuery();
  }

  private static string ReadFixture(string fileName)
  {
    var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "dotnet-output"));
    return File.ReadAllText(Path.Combine(fixtureRoot, fileName));
  }
}

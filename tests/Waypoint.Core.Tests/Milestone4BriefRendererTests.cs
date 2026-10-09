using System.Globalization;
using Waypoint.Core.Brief;
using Waypoint.Core.Extraction;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Core.Tests;

public sealed class Milestone4BriefRendererTests
{
  [Fact]
  public void Render_matches_golden_story_and_budget()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    var time = new FixedTimeProvider(now);

    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection, time);
    var trace = new TraceStore(connection, time);
    var writer = new ClaimWriter(connection, time);

    var seeded = SeedCanonicalStory(repoRoot, graph, trace, writer, connection, now);
    var renderer = new BriefRenderer(connection, new CharsDiv4TokenCounter(), new Ranker(time), time);

    var brief = renderer.RenderForTask(seeded.TaskId);
    Assert.NotNull(brief);
    Assert.StartsWith("## Waypoint resume brief — task:", brief, StringComparison.Ordinal);

    var tokenCount = new CharsDiv4TokenCounter().CountTokens(brief!);
    Assert.True(tokenCount <= 1500, $"Expected <=1500 tokens but got {tokenCount}.");

    var expectedPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "golden", "brief.md");
    expectedPath = Path.GetFullPath(expectedPath);
    var updateGolden = string.Equals(Environment.GetEnvironmentVariable("UPDATE_GOLDEN"), "1", StringComparison.Ordinal);
    if (updateGolden)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
      File.WriteAllText(expectedPath, brief!);
    }

    var expected = File.ReadAllText(expectedPath);
    Assert.Equal(NormalizeNewlines(expected), NormalizeNewlines(brief!));
  }

  [Fact]
  public void Render_stays_within_budget_and_keeps_whole_items_when_open_items_are_large()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection, time);
    var trace = new TraceStore(connection, time);
    var writer = new ClaimWriter(connection, time);

    var seeded = SeedCanonicalStory(repoRoot, graph, trace, writer, connection, time.GetUtcNow());
    for (var i = 1; i <= 200; i++)
    {
      writer.WriteClaims(seeded.TaskId, "sess-m4-002", null, [
        new ExtractedClaim(ClaimKinds.OpenItem, $"Open item {i} remains pending until dependency {i} is cleared.", 0.55, seeded.PricingNodeId, "regex")
      ]);
    }

    var renderer = new BriefRenderer(connection, new CharsDiv4TokenCounter(), new Ranker(time), time);
    var brief = renderer.RenderForTask(seeded.TaskId);
    Assert.NotNull(brief);

    var tokenCount = new CharsDiv4TokenCounter().CountTokens(brief!);
    Assert.True(tokenCount <= 1500, $"Expected <=1500 tokens but got {tokenCount}.");

    var lines = brief!.Split('\n', StringSplitOptions.None);
    var openItemsStart = Array.FindIndex(lines, line => line.Trim().Equals("**Open items / next steps (ordered).**", StringComparison.Ordinal));
    Assert.True(openItemsStart >= 0, "Open items section not found.");

    var openItemLines = lines
      .Skip(openItemsStart + 1)
      .TakeWhile(line => line.Trim().Length > 0)
      .Select(line => line.Trim())
      .Where(line => line.Length > 0 && char.IsDigit(line[0]))
      .ToArray();
    Assert.NotEmpty(openItemLines);
    Assert.All(openItemLines, line =>
    {
      Assert.DoesNotContain(" …", line, StringComparison.Ordinal);
      Assert.DoesNotContain("(conf ", line, StringComparison.Ordinal);
    });
  }

  [Fact]
  public void Render_for_active_task_returns_null_when_there_is_no_active_task()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var renderer = new BriefRenderer(connection);
    var brief = renderer.RenderForActiveTask(repoRoot, "main");
    Assert.Null(brief);
  }

  private static SeededTask SeedCanonicalStory(
    string repoRoot,
    GraphStore graph,
    TraceStore trace,
    ClaimWriter writer,
    Microsoft.Data.Sqlite.SqliteConnection connection,
    DateTimeOffset now)
  {
    var pricingNodeId = graph.UpsertNode(new NodeUpsert(
      "method",
      "PricingService.Apply",
      "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)",
      Project: "Shop.Core",
      Lang: "csharp"));
    var programFileNodeId = graph.UpsertNode(new NodeUpsert(
      "file",
      "src/Shop.Web/Program.cs",
      "file:src/Shop.Web/Program.cs",
      Project: "Shop.Web"));
    var pricingFileNodeId = graph.UpsertNode(new NodeUpsert(
      "file",
      "src/Shop.Core/Services/PricingService.cs",
      "file:src/Shop.Core/Services/PricingService.cs",
      Project: "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("migration", "AddDiscountPolicy", "mig:pending:AddDiscountPolicy", Project: "Shop.Infrastructure"));

    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "#task tighten pricing flow");
    trace.SetSessionTask("sess-m4-001", taskId, "gpt-5.3-codex");
    trace.SetSessionTask("sess-m4-002", taskId, "gpt-5.3-codex");

    for (var i = 0; i < 4; i++)
    {
      var episodeId = trace.AddEpisode("sess-m4-001", "edit", summary: "edit PricingService.Apply");
      InsertTouch(connection, episodeId, pricingNodeId, "edited");
      InsertTouch(connection, episodeId, pricingFileNodeId, "edited");
    }

    for (var i = 0; i < 2; i++)
    {
      var episodeId = trace.AddEpisode("sess-m4-002", "edit", summary: "edit Program.cs");
      InsertTouch(connection, episodeId, programFileNodeId, "edited");
    }

    _ = trace.AddEpisode(
      "sess-m4-002",
      "shell",
      """
      {"command":"dotnet test","exit_code":1,"shell":{"dotnet_command":"test","build":"failed","failed":true,"tests":{"passed":2,"failed":1,"skipped":0,"failed_names":["Shop.UnitTests.PricingTests.Rounds_half_up"]},"ef_database_update":false}}
      """,
      "dotnet test failed");

    writer.WriteClaims(taskId, "sess-m4-001", null, [
      new ExtractedClaim(ClaimKinds.AttemptFailed, "PricingService.Apply -> System.InvalidOperationException: Cannot consume scoped service from singleton.", 0.8, pricingNodeId, "tool_output"),
      new ExtractedClaim(ClaimKinds.AttemptFailed, "PricingService.Apply -> Shop.UnitTests.PricingTests.Rounds_half_up failed.", 0.8, pricingNodeId, "tool_output"),
      new ExtractedClaim(ClaimKinds.Decision, "Switched to AddScoped for IPricingService.", 0.9, pricingNodeId, "explicit"),
      new ExtractedClaim(ClaimKinds.Constraint, "TreatWarningsAsErrors must remain enabled for CI.", 0.75, pricingNodeId, "regex"),
      new ExtractedClaim(ClaimKinds.OpenItem, "Still need to run dotnet ef database update on staging.", 0.65, programFileNodeId, "regex"),
      new ExtractedClaim(ClaimKinds.OpenItem, "TODO: confirm MidpointRounding behavior for cart totals.", 0.6, pricingNodeId, "regex"),
      new ExtractedClaim(ClaimKinds.OpenItem, "Follow-up: add integration coverage around checkout failure path.", 0.55, pricingNodeId, "regex")
    ]);

    return new SeededTask(taskId, pricingNodeId);
  }

  private static void InsertTouch(Microsoft.Data.Sqlite.SqliteConnection connection, long episodeId, long nodeId, string how)
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

  private static string NormalizeNewlines(string value) =>
    value.Replace("\r\n", "\n", StringComparison.Ordinal);

  private sealed record SeededTask(long TaskId, long PricingNodeId);

  private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }
}

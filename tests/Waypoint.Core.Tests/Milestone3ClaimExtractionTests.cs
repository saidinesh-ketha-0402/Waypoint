using Waypoint.Core.Extraction;
using Waypoint.Core.Paths;
using Waypoint.Core.Queries;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Core.Tests;

public sealed class Milestone3ClaimExtractionTests
{
  [Fact]
  public void After_agent_response_extracts_all_claim_kinds_with_symbol_links()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    SeedDemoGraph(graph);

    var responseText = ReadAfterAgentResponseText();
    var classifier = CreateClassifier(connection);
    var claims = classifier.Classify(responseText, source: "regex");

    Assert.Equal(4, claims.Count);
    Assert.Contains(claims, claim => claim.Kind == ClaimKinds.Decision);
    Assert.Contains(claims, claim => claim.Kind == ClaimKinds.Constraint);
    Assert.Contains(claims, claim => claim.Kind == ClaimKinds.AttemptFailed);
    Assert.Contains(claims, claim => claim.Kind == ClaimKinds.OpenItem);
    Assert.All(claims, claim => Assert.NotNull(claim.AboutNodeId));
  }

  [Fact]
  public void Question_sentence_reduces_confidence_to_half_or_lower()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    SeedDemoGraph(graph);

    var classifier = CreateClassifier(connection);
    var claims = classifier.Classify("Should I use `AddSingleton` for `PricingService`?", source: "regex");

    Assert.Single(claims);
    Assert.Equal(ClaimKinds.Decision, claims[0].Kind);
    Assert.True(claims[0].Confidence <= 0.5);
  }

  [Fact]
  public void Near_duplicate_claim_is_deduped_and_confidence_is_raised()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    var trace = new TraceStore(connection);
    SeedDemoGraph(graph);

    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "Keep pricing lifetime stable");
    trace.SetSessionTask("session-m3-a", taskId);
    var writer = new ClaimWriter(connection);

    var first = new ExtractedClaim(
      ClaimKinds.Decision,
      "Decision: keep `PricingService` scoped.",
      0.6,
      graph.NodeIdByQualifiedName("T:Shop.Core.Services.PricingService"),
      "regex");
    var second = new ExtractedClaim(
      ClaimKinds.Decision,
      "Decision keep PricingService scoped",
      0.8,
      graph.NodeIdByQualifiedName("T:Shop.Core.Services.PricingService"),
      "regex");

    var insertedFirst = writer.WriteClaims(taskId, "session-m3-a", null, [first]);
    var insertedSecond = writer.WriteClaims(taskId, "session-m3-a", null, [second]);

    Assert.Equal(1, insertedFirst);
    Assert.Equal(0, insertedSecond);

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT COUNT(*), MAX(confidence)
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'decision';
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(1L, reader.GetInt64(0));
    Assert.Equal(0.8, reader.GetDouble(1), precision: 3);
  }

  [Fact]
  public void Switched_sentence_supersedes_previous_decision_on_same_node()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graph = new GraphStore(connection);
    var trace = new TraceStore(connection);
    SeedDemoGraph(graph);

    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "Set DI lifetime");
    trace.SetSessionTask("session-m3-b", taskId);
    var writer = new ClaimWriter(connection);
    var pricingNodeId = graph.NodeIdByQualifiedName("T:Shop.Core.Services.PricingService");
    Assert.NotNull(pricingNodeId);

    writer.WriteClaims(taskId, "session-m3-b", null, [
      new ExtractedClaim(
        ClaimKinds.Decision,
        "Decision: use AddSingleton for PricingService.",
        0.7,
        pricingNodeId,
        "regex")
    ]);

    writer.WriteClaims(taskId, "session-m3-b", null, [
      new ExtractedClaim(
        ClaimKinds.Decision,
        "Switched to `AddScoped` instead of singleton for `PricingService`.",
        0.9,
        pricingNodeId,
        "regex")
    ]);

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT text, status, superseded_by
      FROM claim
      WHERE task_id = $task_id
        AND kind = 'decision'
      ORDER BY id ASC;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    using var reader = command.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal("superseded", reader.GetString(1));
    Assert.False(reader.IsDBNull(2));
    Assert.True(reader.Read());
    Assert.Equal("current", reader.GetString(1));
    Assert.False(reader.Read());
  }

  [Fact]
  public void Transcript_reader_parses_jsonl_and_plain_text_formats()
  {
    var root = TestRepo.CreateTempDirectory();
    var jsonlPath = Path.Combine(root, "transcript.jsonl");
    File.WriteAllText(jsonlPath, """
{"role":"user","content":"hello"}
{"role":"assistant","content":"Decision: keep PricingService scoped."}
{"role":"tool","output":"dotnet test failed"}
""");

    var plainPath = Path.Combine(root, "transcript.txt");
    File.WriteAllText(plainPath, """
user: hello
assistant: Open item: still need migration cleanup.
tool: Build FAILED with CS0103
""");

    var reader = new TranscriptReader();
    var jsonlBlocks = reader.ReadAssistantAndToolBlocks(jsonlPath);
    var plainBlocks = reader.ReadAssistantAndToolBlocks(plainPath);

    Assert.Equal(2, jsonlBlocks.Count);
    Assert.Equal("assistant", jsonlBlocks[0].Role);
    Assert.Equal("tool", jsonlBlocks[1].Role);

    Assert.Equal(2, plainBlocks.Count);
    Assert.Equal("assistant", plainBlocks[0].Role);
    Assert.Equal("tool", plainBlocks[1].Role);
  }

  private static ClaimClassifier CreateClassifier(Microsoft.Data.Sqlite.SqliteConnection connection)
  {
    var resolver = new SymbolResolver(connection);
    var linker = new SymbolLinker(resolver);
    return new ClaimClassifier(linker, new SentenceSplitter());
  }

  private static string ReadAfterAgentResponseText()
  {
    var fixturePath = Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "..",
      "..",
      "..",
      "..",
      "fixtures",
      "hooks",
      "afterAgentResponse.json"));
    var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixturePath));
    return json.RootElement.GetProperty("response_text").GetString() ?? string.Empty;
  }

  private static void SeedDemoGraph(GraphStore graph)
  {
    graph.UpsertNode(new NodeUpsert("class", "PricingService", "T:Shop.Core.Services.PricingService"));
    graph.UpsertNode(new NodeUpsert("interface", "IPricingService", "T:Shop.Core.Interfaces.IPricingService"));
    graph.UpsertNode(new NodeUpsert("interface", "IShopContext", "T:Shop.Core.Interfaces.IShopContext"));
    graph.UpsertNode(new NodeUpsert("class", "ShopContext", "T:Shop.Infrastructure.ShopContext"));
    graph.UpsertNode(new NodeUpsert("class", "OrdersController", "T:Shop.Web.Controllers.OrdersController"));
    graph.UpsertNode(new NodeUpsert("class", "BasketService", "T:Shop.Core.Services.BasketService"));
    graph.UpsertNode(new NodeUpsert("class", "ShopContextAdapter", "T:Shop.Web.Infrastructure.ShopContextAdapter"));
  }
}

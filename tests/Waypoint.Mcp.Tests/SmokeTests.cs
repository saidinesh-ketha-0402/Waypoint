using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;
using Waypoint.Core.Brief;
using Waypoint.Core.Extraction;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Mcp.Tests;

public sealed class SmokeTests
{
  [Fact]
  public async Task Protocol_tools_list_returns_expected_snake_case_names()
  {
    var repoRoot = TestRepoRoot.Create();
    var cliProjectPath = LocateCliProjectPath();
    var serverConfig = new McpServerConfig
    {
      Id = "waypoint-test",
      Name = "waypoint-test",
      TransportType = TransportTypes.StdIo,
      Location = "dotnet"
    };
    var transportOptions = new StdioClientTransportOptions
    {
      Command = "dotnet",
      Arguments = $"run --project \"{cliProjectPath}\" -p:NuGetAudit=false -- serve --repo \"{repoRoot}\"",
      WorkingDirectory = Path.GetDirectoryName(cliProjectPath)
    };
    await using var client = await McpClientFactory.CreateAsync(
      serverConfig,
      clientOptions: new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(20) },
      createTransportFunc: (_, loggerFactory) => new StdioClientTransport(transportOptions, serverConfig, loggerFactory),
      loggerFactory: NullLoggerFactory.Instance,
      cancellationToken: CancellationToken.None);

    var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
    var toolNames = tools.Select(tool => tool.Name).ToArray();
    var expected = new[]
    {
      "resume",
      "why",
      "impact",
      "what_changed",
      "open_items",
      "record_decision",
      "search"
    };
    Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), toolNames!.OrderBy(name => name, StringComparer.Ordinal));
  }

  [Fact]
  public void Resume_returns_brief_for_active_task()
  {
    var fixture = CreateFixture();
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var result = tools.Resume();

    Assert.StartsWith("## Waypoint resume brief — task:", result, StringComparison.Ordinal);
  }

  [Fact]
  public void Resume_no_task_returns_expected_message()
  {
    var repoRoot = TestRepoRoot.Create();
    using var _ = WaypointDb.Open(WaypointPaths.ForRepo(repoRoot).DbPath);
    var tools = new WaypointTools(new WaypointContext(repoRoot), NullLogger<WaypointTools>.Instance);

    var result = tools.Resume();

    Assert.Contains("No active Waypoint task", result, StringComparison.Ordinal);
  }

  [Fact]
  public void Open_items_can_resolve_item()
  {
    var fixture = CreateFixture();
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var before = tools.OpenItems();
    Assert.Contains("open item 1", before, StringComparison.Ordinal);

    var after = tools.OpenItems(resolve_id: fixture.OpenItemClaimId);
    Assert.Contains($"Resolved #{fixture.OpenItemClaimId}", after, StringComparison.Ordinal);
    Assert.Contains("### open_items", after, StringComparison.Ordinal);
    Assert.Contains("- none", after, StringComparison.Ordinal);
  }

  [Fact]
  public void Record_decision_writes_explicit_claim()
  {
    var fixture = CreateFixture();
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var response = tools.RecordDecision(
      "Use MidpointRounding.AwayFromZero for cart pricing.",
      about: "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)",
      kind: ClaimKinds.Decision);

    Assert.Contains("Recorded decision", response, StringComparison.Ordinal);
    var why = tools.Why("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)");
    Assert.Contains("MidpointRounding.AwayFromZero", why, StringComparison.Ordinal);
  }

  [Fact]
  public void Why_ambiguous_lists_candidates()
  {
    var fixture = CreateFixture(withAmbiguousInterfaceNames: true);
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var why = tools.Why("IPricingService");

    Assert.Contains("ambiguous symbol", why, StringComparison.Ordinal);
    Assert.Contains("Shop.Core.Interfaces.IPricingService", why, StringComparison.Ordinal);
    Assert.Contains("Shop.Legacy.Interfaces.IPricingService", why, StringComparison.Ordinal);
  }

  [Fact]
  public void Impact_IPricingService_lists_consumers_and_endpoints()
  {
    var fixture = CreateFixture();
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var impact = tools.Impact("IPricingService");

    Assert.Contains("### impact: IPricingService (interface · Shop.Core)", impact, StringComparison.Ordinal);
    Assert.Contains("**Implementations / registrations**", impact, StringComparison.Ordinal);
    Assert.Contains("PricingService", impact, StringComparison.Ordinal);
    Assert.Contains("AddScoped", impact, StringComparison.Ordinal);
    Assert.Contains("**Injected by**", impact, StringComparison.Ordinal);
    Assert.Contains("BasketService (Shop.Core) → BasketController (Shop.Web)", impact, StringComparison.Ordinal);
    Assert.Contains("OrderService (Shop.Core) → OrdersController (Shop.Web)", impact, StringComparison.Ordinal);
    Assert.Contains("**Endpoints reaching it**", impact, StringComparison.Ordinal);
    Assert.Contains("→ BasketController.Checkout", impact, StringComparison.Ordinal);
    Assert.Contains("POST /api/basket/checkout", impact, StringComparison.Ordinal);
    Assert.Contains("POST /api/orders", impact, StringComparison.Ordinal);
  }

  [Fact]
  public void Impact_reports_implementation_from_registration_even_without_implements_edge()
  {
    var fixture = CreateFixture(withImplementsEdge: false);
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var impact = tools.Impact("IPricingService");

    Assert.Contains("**Implementations / registrations**", impact, StringComparison.Ordinal);
    Assert.Contains("PricingService", impact, StringComparison.Ordinal);
    Assert.Contains("AddScoped", impact, StringComparison.Ordinal);
  }

  [Fact]
  public void Impact_depth_is_clamped_to_max_4()
  {
    var fixture = CreateFixture();
    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);

    var clamped = tools.Impact("IPricingService", depth: 4);
    var oversized = tools.Impact("IPricingService", depth: 99);

    Assert.Equal(clamped, oversized);
  }

  [Fact]
  public void Outputs_are_bounded_to_800_tokens()
  {
    var fixture = CreateFixture();
    using (var connection = WaypointDb.Open(WaypointPaths.ForRepo(fixture.RepoRoot).DbPath))
    {
      var writer = new ClaimWriter(connection);
      for (var i = 0; i < 500; i++)
      {
        writer.WriteClaims(fixture.TaskId, "sess-mcp-1", null, [
          new ExtractedClaim(ClaimKinds.OpenItem, $"Open item {i} remains pending and includes extra text to increase output size significantly. This line is intentionally long to force truncation and token budget clipping across multiple entries in the output for verification.", 0.7, fixture.PricingNodeId, "regex")
        ]);
      }
    }

    var tools = new WaypointTools(new WaypointContext(fixture.RepoRoot), NullLogger<WaypointTools>.Instance);
    var counter = new CharsDiv4TokenCounter();
    var output = tools.OpenItems();

    Assert.True(counter.CountTokens(output) <= 800, $"Expected <= 800 tokens, got {counter.CountTokens(output)}.");
  }

  private static SeededFixture CreateFixture(bool withAmbiguousInterfaceNames = false, bool withImplementsEdge = true)
  {
    var repoRoot = TestRepoRoot.Create();
    var paths = WaypointPaths.ForRepo(repoRoot);
    using var connection = WaypointDb.Open(paths.DbPath);
    var graph = new GraphStore(connection);
    var trace = new TraceStore(connection);
    var writer = new ClaimWriter(connection);

    var pricingNode = graph.UpsertNode(new NodeUpsert(
      "method",
      "PricingService.Apply",
      "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)",
      Project: "Shop.Core",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "interface",
      "IPricingService",
      "T:Shop.Core.Interfaces.IPricingService",
      Project: "Shop.Core",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "class",
      "PricingService",
      "T:Shop.Core.Services.PricingService",
      Project: "Shop.Core",
      Lang: "csharp",
      MetaJson: """{"lifetime":"Scoped"}"""));
    _ = graph.UpsertNode(new NodeUpsert(
      "class",
      "BasketService",
      "T:Shop.Core.Services.BasketService",
      Project: "Shop.Core",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "class",
      "OrderService",
      "T:Shop.Core.Services.OrderService",
      Project: "Shop.Core",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "class",
      "BasketController",
      "T:Shop.Web.Controllers.BasketController",
      Project: "Shop.Web",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "class",
      "OrdersController",
      "T:Shop.Web.Controllers.OrdersController",
      Project: "Shop.Web",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "method",
      "Checkout",
      "M:Shop.Web.Controllers.BasketController.Checkout()",
      Project: "Shop.Web",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "method",
      "Create",
      "M:Shop.Web.Controllers.OrdersController.Create()",
      Project: "Shop.Web",
      Lang: "csharp"));
    _ = graph.UpsertNode(new NodeUpsert(
      "endpoint",
      "POST /api/basket/checkout",
      "ep:POST /api/basket/checkout",
      Project: "Shop.Web"));
    _ = graph.UpsertNode(new NodeUpsert(
      "endpoint",
      "POST /api/orders",
      "ep:POST /api/orders",
      Project: "Shop.Web"));
    if (withAmbiguousInterfaceNames)
    {
      _ = graph.UpsertNode(new NodeUpsert(
        "interface",
        "IPricingService",
        "T:Shop.Legacy.Interfaces.IPricingService",
        Project: "Shop.Legacy",
        Lang: "csharp"));
    }

    var programNode = graph.UpsertNode(new NodeUpsert(
      "file",
      "src/Shop.Web/Program.cs",
      "file:src/Shop.Web/Program.cs",
      Project: "Shop.Web"));
    if (withImplementsEdge)
    {
      graph.UpsertEdge(new EdgeUpsert("T:Shop.Core.Services.PricingService", "T:Shop.Core.Interfaces.IPricingService", "IMPLEMENTS"));
    }
    graph.UpsertEdge(new EdgeUpsert(
      "T:Shop.Core.Interfaces.IPricingService",
      "T:Shop.Core.Services.PricingService",
      "REGISTERED_AS",
      OriginFile: "src/Shop.Web/Program.cs"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Core.Services.BasketService", "T:Shop.Core.Interfaces.IPricingService", "INJECTS"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Core.Services.OrderService", "T:Shop.Core.Interfaces.IPricingService", "INJECTS"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Web.Controllers.BasketController", "T:Shop.Core.Services.BasketService", "INJECTS"));
    graph.UpsertEdge(new EdgeUpsert("T:Shop.Web.Controllers.OrdersController", "T:Shop.Core.Services.OrderService", "INJECTS"));
    graph.UpsertEdge(new EdgeUpsert("M:Shop.Web.Controllers.BasketController.Checkout()", "T:Shop.Web.Controllers.BasketController", "CONTAINS"));
    graph.UpsertEdge(new EdgeUpsert("M:Shop.Web.Controllers.OrdersController.Create()", "T:Shop.Web.Controllers.OrdersController", "CONTAINS"));
    graph.UpsertEdge(new EdgeUpsert("ep:POST /api/basket/checkout", "M:Shop.Web.Controllers.BasketController.Checkout()", "ROUTES_TO"));
    graph.UpsertEdge(new EdgeUpsert("ep:POST /api/orders", "M:Shop.Web.Controllers.OrdersController.Create()", "ROUTES_TO"));

    var taskId = trace.EnsureTaskForPrompt(repoRoot, "main", "#task tighten pricing flow");
    trace.SetSessionTask("sess-mcp-1", taskId, "gpt-5.3-codex");
    var episodeId = trace.AddEpisode("sess-mcp-1", "edit", summary: "edit pricing");
    InsertTouch(connection, episodeId, pricingNode, "edited");
    InsertTouch(connection, episodeId, programNode, "edited");

    writer.WriteClaims(taskId, "sess-mcp-1", null, [
      new ExtractedClaim(ClaimKinds.Decision, "Switched to AddScoped for IPricingService.", 0.9, pricingNode, "explicit"),
      new ExtractedClaim(ClaimKinds.OpenItem, "open item 1", 0.8, pricingNode, "regex"),
      new ExtractedClaim(ClaimKinds.Constraint, "Keep TreatWarningsAsErrors enabled.", 0.7, programNode, "regex")
    ]);

    var openItemClaimId = FindClaimId(connection, taskId, ClaimKinds.OpenItem, "open item 1")
      ?? throw new InvalidOperationException("Expected seeded open item.");
    return new SeededFixture(repoRoot, taskId, pricingNode, openItemClaimId);
  }

  private static void InsertTouch(SqliteConnection connection, long episodeId, long nodeId, string how)
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

  private static long? FindClaimId(SqliteConnection connection, long taskId, string kind, string text)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT id
      FROM claim
      WHERE task_id = $task_id AND kind = $kind AND text = $text
      ORDER BY id DESC
      LIMIT 1;
      """;
    command.Parameters.AddWithValue("$task_id", taskId);
    command.Parameters.AddWithValue("$kind", kind);
    command.Parameters.AddWithValue("$text", text);
    var value = command.ExecuteScalar();
    return value is long id ? id : null;
  }

  private sealed record SeededFixture(string RepoRoot, long TaskId, long PricingNodeId, long OpenItemClaimId);

  private static string LocateCliProjectPath()
  {
    var start = new DirectoryInfo(AppContext.BaseDirectory);
    while (start is not null)
    {
      var candidate = Path.Combine(start.FullName, "src", "Waypoint.Cli", "Waypoint.Cli.csproj");
      if (File.Exists(candidate))
      {
        return candidate;
      }

      start = start.Parent;
    }

    throw new InvalidOperationException("Could not locate src/Waypoint.Cli/Waypoint.Cli.csproj from test base directory.");
  }
}

internal static class TestRepoRoot
{
  public static string Create()
  {
    var root = Path.Combine(Path.GetTempPath(), "waypoint-mcp-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var git = Path.Combine(root, ".git");
    Directory.CreateDirectory(git);
    File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main");
    return root;
  }
}

using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Core.Tests;

public sealed class Milestone1StoreTests
{
  [Fact]
  public void Open_creates_schema()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;

    using var connection = WaypointDb.Open(dbPath);

    var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var command = connection.CreateCommand())
    {
      command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
      using var reader = command.ExecuteReader();
      while (reader.Read())
      {
        tableNames.Add(reader.GetString(0));
      }
    }

    var expectedTables = new[]
    {
      "meta", "node", "span", "edge", "task", "session", "episode", "episode_touch", "claim", "episode_fts", "claim_fts"
    };
    foreach (var expected in expectedTables)
    {
      Assert.Contains(expected, tableNames);
    }

    using (var pragmaCommand = connection.CreateCommand())
    {
      pragmaCommand.CommandText = "PRAGMA journal_mode;";
      var mode = pragmaCommand.ExecuteScalar() as string;
      Assert.Equal("wal", mode);
    }

    using (var metaCommand = connection.CreateCommand())
    {
      metaCommand.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
      var version = metaCommand.ExecuteScalar() as string;
      Assert.Equal("1", version);
    }
  }

  [Fact]
  public void Open_twice_is_idempotent()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;

    using var first = WaypointDb.Open(dbPath);
    using var second = WaypointDb.Open(dbPath);

    using var command = second.CreateCommand();
    command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
    Assert.Equal("1", command.ExecuteScalar() as string);
  }

  [Fact]
  public void Worktree_git_file_is_followed()
  {
    var root = TestRepo.CreateTempDirectory();
    var externalGitDir = Path.Combine(root, "real-git-dir");
    Directory.CreateDirectory(externalGitDir);
    File.WriteAllText(Path.Combine(externalGitDir, "HEAD"), "ref: refs/heads/feature/worktree");
    File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {externalGitDir}");

    var paths = WaypointPaths.ForRepo(root);

    Assert.StartsWith(Path.Combine(externalGitDir, "waypoint"), paths.StoreRoot, StringComparison.Ordinal);
    Assert.Equal(Path.Combine(externalGitDir, "waypoint", "graph.db"), paths.DbPath);
  }

  [Fact]
  public void Branch_from_head_reads_branch_and_detached()
  {
    var branchRepo = TestRepo.CreateGitRepo();
    File.WriteAllText(Path.Combine(branchRepo, ".git", "HEAD"), "ref: refs/heads/feature/discounts");
    Assert.Equal("feature/discounts", GitInfo.CurrentBranch(branchRepo));

    var detachedRepo = TestRepo.CreateGitRepo();
    File.WriteAllText(Path.Combine(detachedRepo, ".git", "HEAD"), "abc1234feedface000011112222333344445555");
    Assert.Equal("detached@abc1234", GitInfo.CurrentBranch(detachedRepo));
  }

  [Fact]
  public void SpanIndex_returns_innermost()
  {
    var repo = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repo).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graphStore = new GraphStore(connection);
    var spanIndex = new SpanIndex(connection);

    graphStore.UpsertNode(new NodeUpsert("class", "PricingService", "T:Shop.Core.Services.PricingService"));
    graphStore.UpsertNode(new NodeUpsert("method", "Apply", "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)"));
    graphStore.ReplaceFile(
      "src/Shop.Core/Services/PricingService.cs",
      [],
      [
        new SpanUpsert("T:Shop.Core.Services.PricingService", "src/Shop.Core/Services/PricingService.cs", 1, 40),
        new SpanUpsert("M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)", "src/Shop.Core/Services/PricingService.cs", 10, 20)
      ],
      []);

    var methodMatch = spanIndex.FindEnclosing("src/Shop.Core/Services/PricingService.cs", 12);
    Assert.NotNull(methodMatch);
    Assert.Equal("method", methodMatch!.Kind);

    var classMatch = spanIndex.FindEnclosing("src/Shop.Core/Services/PricingService.cs", 30);
    Assert.NotNull(classMatch);
    Assert.Equal("class", classMatch!.Kind);
  }

  [Fact]
  public void ReplaceFile_is_atomic_for_spans_and_edges()
  {
    var repo = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repo).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var graphStore = new GraphStore(connection);

    graphStore.UpsertNode(new NodeUpsert("class", "A", "T:A"));
    graphStore.UpsertNode(new NodeUpsert("class", "B", "T:B"));
    graphStore.UpsertEdge(new EdgeUpsert("T:A", "T:B", "REFERENCES", OriginFile: "src/A.cs"));
    graphStore.UpsertEdge(new EdgeUpsert("T:B", "T:A", "REFERENCES", OriginFile: "src/B.cs"));
    graphStore.ReplaceSpans(graphStore.NodeIdByQualifiedName("T:A")!.Value, [new SpanUpsert("T:A", "src/A.cs", 1, 10)]);
    graphStore.ReplaceSpans(graphStore.NodeIdByQualifiedName("T:B")!.Value, [new SpanUpsert("T:B", "src/B.cs", 1, 10)]);

    graphStore.ReplaceFile(
      "src/A.cs",
      [new NodeUpsert("class", "A", "T:A")],
      [new SpanUpsert("T:A", "src/A.cs", 2, 8)],
      []);

    using var spanCountCommand = connection.CreateCommand();
    spanCountCommand.CommandText = "SELECT COUNT(*) FROM span WHERE file_path = 'src/A.cs' AND start_line = 2 AND end_line = 8;";
    Assert.Equal(1L, spanCountCommand.ExecuteScalar());

    using var oldSpanCommand = connection.CreateCommand();
    oldSpanCommand.CommandText = "SELECT COUNT(*) FROM span WHERE file_path = 'src/A.cs' AND start_line = 1 AND end_line = 10;";
    Assert.Equal(0L, oldSpanCommand.ExecuteScalar());

    using var removedEdgeCommand = connection.CreateCommand();
    removedEdgeCommand.CommandText = "SELECT COUNT(*) FROM edge WHERE origin_file = 'src/A.cs';";
    Assert.Equal(0L, removedEdgeCommand.ExecuteScalar());

    using var retainedEdgeCommand = connection.CreateCommand();
    retainedEdgeCommand.CommandText = "SELECT COUNT(*) FROM edge WHERE origin_file = 'src/B.cs';";
    Assert.Equal(1L, retainedEdgeCommand.ExecuteScalar());
  }

  [Fact]
  public async Task Concurrent_writers_do_not_surface_sqlite_busy()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using (WaypointDb.Open(dbPath))
    {
    }

    var writers = Enumerable.Range(0, 4)
      .Select(writerId => Task.Run(() =>
      {
        using var connection = WaypointDb.Open(dbPath);
        var store = new TraceStore(connection);
        var sessionId = $"session-{writerId}";
        store.EnsureSession(sessionId);

        for (var i = 0; i < 100; i++)
        {
          store.AddEpisode(sessionId, "shell", summary: $"writer-{writerId}-{i}");
        }
      }));

    await Task.WhenAll(writers);

    using var verifyConnection = WaypointDb.Open(dbPath);
    using var countCommand = verifyConnection.CreateCommand();
    countCommand.CommandText = "SELECT COUNT(*) FROM episode;";
    Assert.Equal(400L, countCommand.ExecuteScalar());
  }

  [Fact]
  public void New_task_prefix_pauses_previous()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var store = new TraceStore(connection);

    var firstTaskId = store.EnsureTaskForPrompt(repoRoot, "main", "Implement discount service");
    var secondTaskId = store.EnsureTaskForPrompt(repoRoot, "main", "new task: Add resilient order retries");

    Assert.NotEqual(firstTaskId, secondTaskId);
    Assert.Equal("paused", store.GetTaskStatus(firstTaskId));
    Assert.Equal("active", store.GetTaskStatus(secondTaskId));
  }

  [Fact]
  public void Session_ordinal_skips_empty_sessions()
  {
    var repoRoot = TestRepo.CreateGitRepo();
    var dbPath = WaypointPaths.ForRepo(repoRoot).DbPath;
    using var connection = WaypointDb.Open(dbPath);
    var store = new TraceStore(connection);

    var taskId = store.EnsureTaskForPrompt(repoRoot, "main", "Implement pricing");
    store.EnsureSession("s1", taskId);
    store.AddEpisode("s1", "prompt");

    store.EnsureSession("s2", taskId);

    store.EnsureSession("s3", taskId);
    store.AddEpisode("s3", "response");

    Assert.Equal(1, store.GetSessionOrdinal("s1"));
    Assert.Equal(2, store.GetSessionOrdinal("s3"));
  }
}

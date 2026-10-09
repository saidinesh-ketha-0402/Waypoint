using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Cli;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Roslyn.Semantic;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class HookRunnerMilestone5Tests
{
  [Fact]
  public async Task AfterAgentResponse_extracts_claims()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m5_001",
        "session_id": "sess_m5_001",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task stabilize pricing behavior"
      }
      """);

    var responsePayload = $$"""
      {
        "conversation_id": "conv_m5_001",
        "session_id": "sess_m5_001",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "afterAgentResponse",
        "response_text": "Decision: keep `PricingService` scoped to avoid cross-request state. Constraint: `ShopContext` remains scoped and must never be captured by singleton services. Failed attempt: switching `IPricingService` to singleton broke startup because of scoped `IShopContext`. Open item: decide whether `OrdersController` should persist through `BasketService` or directly through `ShopContextAdapter`."
      }
      """;

    var result = await ExecuteHookAsync("afterAgentResponse", responsePayload);
    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);

    var kinds = fixture.StringList("""
      SELECT kind
      FROM claim
      WHERE task_id = (
        SELECT id FROM task WHERE status = 'active' ORDER BY id DESC LIMIT 1
      )
      ORDER BY kind ASC;
      """);
    Assert.Contains("decision", kinds);
    Assert.Contains("constraint", kinds);
    Assert.Contains("attempt_failed", kinds);
    Assert.Contains("open_item", kinds);

    var claimsWithNode = fixture.Scalar("""
      SELECT COUNT(*)
      FROM claim
      WHERE task_id = (
        SELECT id FROM task WHERE status = 'active' ORDER BY id DESC LIMIT 1
      )
        AND about_node_id IS NOT NULL;
      """);
    Assert.Equal(4L, claimsWithNode);
  }

  [Fact]
  public async Task SessionStart_returns_real_brief_after_work()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m5_002",
        "session_id": "sess_m5_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task add discount endpoint"
      }
      """);

    _ = await ExecuteHookAsync("afterAgentResponse", $$"""
      {
        "conversation_id": "conv_m5_002",
        "session_id": "sess_m5_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "afterAgentResponse",
        "response_text": "Decision: use `AddScoped` for `IPricingService`. Open item: still need migration."
      }
      """);

    var startResult = await ExecuteHookAsync("sessionStart", $$"""
      {
        "conversation_id": "conv_m5_002",
        "session_id": "sess_m5_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "sessionStart"
      }
      """);

    Assert.Equal(0, startResult.ExitCode);
    Assert.Contains("\"additional_context\":\"## Waypoint resume brief", startResult.StdOut, StringComparison.Ordinal);
    Assert.Contains("\"WAYPOINT_TASK_ID\":\"", startResult.StdOut, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Fallback_injects_brief_once()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    _ = await ExecuteHookAsync("beforeSubmitPrompt", $$"""
      {
        "conversation_id": "conv_m5_003",
        "session_id": "sess_m5_004",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task test fallback brief path"
      }
      """);

    var postToolPayload = $$"""
      {
        "conversation_id": "conv_m5_003",
        "session_id": "sess_m5_005",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "postToolUse",
        "tool_name": "Glob",
        "tool_input": { "glob_pattern": "**/*.cs" },
        "tool_output": "src/Shop.Core/Services/PricingService.cs\nsrc/Shop.Web/Program.cs"
      }
      """;

    var first = await ExecuteHookAsync("postToolUse", postToolPayload, new HookRunnerOptions(InjectBriefOnce: true));
    var second = await ExecuteHookAsync("postToolUse", postToolPayload, new HookRunnerOptions(InjectBriefOnce: true));

    Assert.Equal(0, first.ExitCode);
    Assert.Contains("\"additional_context\":\"## Waypoint resume brief", first.StdOut, StringComparison.Ordinal);
    Assert.Equal(0, second.ExitCode);
    Assert.Equal("{}\n", second.StdOut);

    var mode = fixture.String("""
      SELECT brief_injected_via
      FROM session
      WHERE id = 'sess_m5_005';
      """);
    Assert.Equal("postToolUse", mode);
  }

  private static async Task<HookExecutionResult> ExecuteHookAsync(string eventName, string payload, HookRunnerOptions? options = null)
  {
    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    await using var output = new MemoryStream();

    var exitCode = await HookRunner.RunAsync(
      eventName,
      input,
      output,
      options ?? new HookRunnerOptions());

    return new HookExecutionResult(exitCode, Encoding.UTF8.GetString(output.ToArray()));
  }

  private sealed record HookExecutionResult(int ExitCode, string StdOut);

  private sealed class DemoShopFixture : IAsyncDisposable
  {
    private readonly string _root;
    private SqliteConnection? _connection;

    private DemoShopFixture(string root)
    {
      _root = root;
    }

    public string RepoRoot => _root;

    public static Task<DemoShopFixture> CreateAsync()
    {
      var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "DemoShop"));
      var destinationRoot = Path.Combine(Path.GetTempPath(), "waypoint-cli-m5-tests", Guid.NewGuid().ToString("N"));
      CopyDirectory(sourceRoot, destinationRoot);
      Directory.CreateDirectory(Path.Combine(destinationRoot, ".git"));
      File.WriteAllText(Path.Combine(destinationRoot, ".git", "HEAD"), "ref: refs/heads/main");
      return Task.FromResult(new DemoShopFixture(destinationRoot));
    }

    public async Task IndexAsync()
    {
      var indexer = new SemanticIndexer();
      await indexer.IndexAsync(new IndexOptions(_root)
      {
        SolutionPath = "DemoShop.slnx",
        Mode = IndexMode.MsBuild
      });
    }

    public string String(string sql, params (string Name, object Value)[] parameters)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      return command.ExecuteScalar() as string ?? string.Empty;
    }

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<string> StringList(string sql, params (string Name, object Value)[] parameters)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      var values = new List<string>();
      using var reader = command.ExecuteReader();
      while (reader.Read())
      {
        values.Add(reader.GetString(0));
      }

      return values;
    }

    public ValueTask DisposeAsync()
    {
      _connection?.Dispose();
      if (Directory.Exists(_root))
      {
        Directory.Delete(_root, recursive: true);
      }

      return ValueTask.CompletedTask;
    }

    private static void CopyDirectory(string source, string destination)
    {
      Directory.CreateDirectory(destination);

      foreach (var directory in Directory.GetDirectories(source))
      {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" or ".git")
        {
          continue;
        }

        CopyDirectory(directory, Path.Combine(destination, name));
      }

      foreach (var file in Directory.GetFiles(source))
      {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
      }
    }
  }
}

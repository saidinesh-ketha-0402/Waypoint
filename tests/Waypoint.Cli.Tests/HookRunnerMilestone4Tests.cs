using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Cli;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Roslyn.Semantic;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class HookRunnerMilestone4Tests
{
  [Fact]
  public async Task PostToolUse_grep_maps_lines_to_symbols()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var payload = $$"""
      {
        "conversation_id": "conv_m4_001",
        "session_id": "sess_m4_001",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "postToolUse",
        "tool_name": "Grep",
        "tool_input": { "pattern": "IPricingService" },
        "tool_output": "src/Shop.Core/Interfaces/IPricingService.cs:3:public interface IPricingService\nsrc/Shop.Web/Program.cs:11:builder.Services.AddScoped<IPricingService, PricingService>();"
      }
      """;

    var result = await ExecuteHookAsync("postToolUse", payload);
    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);

    var matched = fixture.StringList("""
      SELECT n.qualified_name
      FROM episode_touch t
      JOIN node n ON n.id = t.node_id
      WHERE t.how = 'matched'
      ORDER BY n.qualified_name;
      """);
    Assert.Contains("T:Shop.Core.Interfaces.IPricingService", matched);
  }

  [Fact]
  public async Task PostToolUse_shell_test_fail_stages_attempt()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var promptPayload = $$"""
      {
        "conversation_id": "conv_m4_002",
        "session_id": "sess_m4_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task stabilize pricing tests"
      }
      """;
    _ = await ExecuteHookAsync("beforeSubmitPrompt", promptPayload);

    var pricingNodeId = fixture.Scalar("""
      SELECT id
      FROM node
      WHERE qualified_name = 'M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)'
      LIMIT 1;
      """);
    fixture.ExecuteNonQuery("""
      INSERT INTO episode(session_id, kind, ts, payload_json, summary)
      VALUES('sess_m4_002', 'edit', '2026-10-06T00:00:00.0000000Z', '{"file":"src/Shop.Core/Services/PricingService.cs"}', 'edit PricingService.cs');
      """);
    var editEpisodeId = fixture.Scalar("SELECT MAX(id) FROM episode WHERE session_id = 'sess_m4_002';");
    fixture.ExecuteNonQuery("""
      INSERT INTO episode_touch(episode_id, node_id, how)
      VALUES($episode_id, $node_id, 'edited');
      """, ("$episode_id", editEpisodeId), ("$node_id", pricingNodeId));

    var toolPayload = $$"""
      {
        "conversation_id": "conv_m4_002",
        "session_id": "sess_m4_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "postToolUse",
        "tool_name": "Shell",
        "tool_input": { "command": "dotnet test" },
        "tool_output": "[xUnit.net 00:00:00.08]     Shop.UnitTests.PricingTests.Clamps_to_50_percent [FAIL]\nFailed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 218 ms - Shop.UnitTests.dll (net10.0)"
      }
      """;
    var result = await ExecuteHookAsync("postToolUse", toolPayload);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);
    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM claim
      WHERE kind = 'attempt_failed'
        AND source = 'tool_output'
        AND text LIKE '%Clamps_to_50_percent failed';
      """));
  }

  [Fact]
  public async Task PostToolUse_shell_di_exception_stages_attempt()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var promptPayload = $$"""
      {
        "conversation_id": "conv_m4_003",
        "session_id": "sess_m4_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "beforeSubmitPrompt",
        "prompt": "#task investigate DI registration issue"
      }
      """;
    _ = await ExecuteHookAsync("beforeSubmitPrompt", promptPayload);

    var toolPayload = $$"""
      {
        "conversation_id": "conv_m4_003",
        "session_id": "sess_m4_003",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "postToolUse",
        "tool_name": "Shell",
        "tool_input": { "command": "dotnet run --project src/Shop.Web" },
        "tool_output": "Unhandled exception. System.AggregateException: Some services are not able to be constructed ...\nSystem.InvalidOperationException: ScopedInSingletonException, Shop.Core.Interfaces.IShopContext, Shop.Core.Interfaces.IPricingService, scoped, singleton"
      }
      """;
    var result = await ExecuteHookAsync("postToolUse", toolPayload);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);
    var text = fixture.String("""
      SELECT text
      FROM claim
      WHERE kind = 'attempt_failed'
      ORDER BY id DESC
      LIMIT 1;
      """);
    Assert.Contains("ScopedInSingletonException", text);
  }

  private static async Task<HookExecutionResult> ExecuteHookAsync(string eventName, string payload)
  {
    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    await using var output = new MemoryStream();

    var exitCode = await HookRunner.RunAsync(
      eventName,
      input,
      output,
      new HookRunnerOptions());

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
      var destinationRoot = Path.Combine(Path.GetTempPath(), "waypoint-cli-m4-tests", Guid.NewGuid().ToString("N"));
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

    public void ExecuteNonQuery(string sql, params (string Name, object Value)[] parameters)
    {
      _connection ??= WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      using var command = _connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      command.ExecuteNonQuery();
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

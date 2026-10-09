using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Cli;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Roslyn.Semantic;
using Xunit;

namespace Waypoint.Cli.Tests;

public sealed class HookRunnerMilestone3Tests
{
  [Fact]
  public async Task AfterFileEdit_cs_touches_method_symbol()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var pricingPath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");
    var original = await File.ReadAllTextAsync(pricingPath);
    const string oldSnippet = "Math.Clamp(discountPct, 0m, DiscountPolicy.MaxDiscountPct)";
    const string newSnippet = "Math.Clamp(discountPct, 0m, DiscountPolicy.MaxDiscountPct - 0m)";
    Assert.Contains(oldSnippet, original, StringComparison.Ordinal);
    var updated = original.Replace(oldSnippet, newSnippet, StringComparison.Ordinal);
    await File.WriteAllTextAsync(pricingPath, updated);

    var payload = $$"""
      {
        "conversation_id": "conv_m3_001",
        "session_id": "sess_m3_001",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "afterFileEdit",
        "file_path": "{{pricingPath}}",
        "edits": [{"old_string":"{{oldSnippet}}","new_string":"{{newSnippet}}"}]
      }
      """;
    var result = await ExecuteHookAsync("afterFileEdit", payload);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);
    var editedQualifiedNames = fixture.StringList("""
      SELECT n.qualified_name
      FROM episode_touch t
      JOIN node n ON n.id = t.node_id
      WHERE t.how = 'edited'
      ORDER BY n.qualified_name;
      """);
    Assert.Contains(
      "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)",
      editedQualifiedNames);
  }

  [Fact]
  public async Task AfterFileEdit_non_cs_touches_file_node()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();

    var projectPath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Shop.Core.csproj");
    var updated = await File.ReadAllTextAsync(projectPath) + Environment.NewLine + "<!-- touched -->";
    await File.WriteAllTextAsync(projectPath, updated);

    var payload = $$"""
      {
        "conversation_id": "conv_m3_002",
        "session_id": "sess_m3_002",
        "workspace_roots": ["{{fixture.RepoRoot}}"],
        "hook_event_name": "afterFileEdit",
        "file_path": "{{projectPath}}",
        "edits": [{"old_string":"","new_string":"<!-- touched -->"}]
      }
      """;
    var result = await ExecuteHookAsync("afterFileEdit", payload);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("{}\n", result.StdOut);
    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM episode_touch t
      JOIN node n ON n.id = t.node_id
      WHERE t.how = 'edited'
        AND n.qualified_name = 'file:src/Shop.Core/Shop.Core.csproj';
      """));
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
      var destinationRoot = Path.Combine(Path.GetTempPath(), "waypoint-cli-m3-tests", Guid.NewGuid().ToString("N"));
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

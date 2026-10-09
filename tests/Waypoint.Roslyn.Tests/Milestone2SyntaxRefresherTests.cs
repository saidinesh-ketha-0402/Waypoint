using Microsoft.Data.Sqlite;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Roslyn.Semantic;
using Waypoint.Roslyn.Syntax;
using Xunit;

namespace Waypoint.Roslyn.Tests;

public sealed class Milestone2SyntaxRefresherTests
{
  [Fact]
  public async Task Synthesised_docid_matches_semantic_for_pricing_method()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");
    var text = await File.ReadAllTextAsync(filePath);
    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Core/Services/PricingService.cs", text));

    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM node
      WHERE qualified_name = 'M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)';
      """));
  }

  [Fact]
  public async Task Refresh_new_method_inserts_node()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");
    var text = await File.ReadAllTextAsync(filePath);
    var insertion = Environment.NewLine + "  public decimal Discount(decimal x) => x;" + Environment.NewLine;
    var insertAt = text.LastIndexOf('}');
    Assert.True(insertAt > 0, "Expected a type-closing brace in PricingService.cs.");
    text = text.Insert(insertAt, insertion);
    await File.WriteAllTextAsync(filePath, text);

    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Core/Services/PricingService.cs", text));

    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM node
      WHERE name = 'Discount'
        AND json_extract(meta_json, '$.syntax_only') = 1;
      """));
  }

  [Fact]
  public async Task Refresh_rename_reconciles_by_span()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");
    var text = await File.ReadAllTextAsync(filePath);
    var renamed = text.Replace(
      "public decimal Apply(decimal subtotal, decimal discount)",
      "public decimal ApplyDiscount(decimal subtotal, decimal discount)",
      StringComparison.Ordinal);
    await File.WriteAllTextAsync(filePath, renamed);

    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Core/Services/PricingService.cs", renamed));

    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*) FROM node
      WHERE qualified_name = 'M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)';
      """));
    Assert.Equal(0L, fixture.Scalar("""
      SELECT COUNT(*) FROM node
      WHERE qualified_name = 'M:Shop.Core.Services.PricingService.ApplyDiscount(System.Decimal,System.Decimal)';
      """));
  }

  [Fact]
  public async Task Refresh_parse_error_keeps_nodes()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");
    var broken = "namespace Shop.Core.Services; public class PricingService { public decimal Broken( => ;";
    await File.WriteAllTextAsync(filePath, broken);
    var nodeCountBefore = fixture.Scalar("SELECT COUNT(*) FROM node;");

    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Core/Services/PricingService.cs", broken));

    Assert.Equal(nodeCountBefore, fixture.Scalar("SELECT COUNT(*) FROM node;"));
  }

  [Fact]
  public async Task Refresh_marks_semantic_edges_stale()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "BasketService.cs");
    var text = await File.ReadAllTextAsync(filePath);
    text += Environment.NewLine + "// stale marker" + Environment.NewLine;
    await File.WriteAllTextAsync(filePath, text);

    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Core/Services/BasketService.cs", text));

    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM edge e
      JOIN node src ON src.id = e.src
      JOIN node dst ON dst.id = e.dst
      WHERE e.kind = 'INJECTS'
        AND e.origin_file = 'src/Shop.Core/Services/BasketService.cs'
        AND src.qualified_name = 'T:Shop.Core.Services.BasketService'
        AND dst.qualified_name = 'T:Shop.Core.Interfaces.IPricingService'
        AND e.stale = 1;
      """));
  }

  [Fact]
  public async Task Refresh_detects_new_registration()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Web", "Program.cs");
    var text = await File.ReadAllTextAsync(filePath);
    var updated = text + Environment.NewLine + "builder.Services.AddSingleton<IPricingService, PricingService>();";
    await File.WriteAllTextAsync(filePath, updated);

    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Web/Program.cs", updated));

    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM edge e
      JOIN node src ON src.id = e.src
      JOIN node dst ON dst.id = e.dst
      WHERE e.kind = 'REGISTERED_AS'
        AND e.origin_file = 'src/Shop.Web/Program.cs'
        AND src.qualified_name = 'T:Shop.Core.Interfaces.IPricingService'
        AND dst.qualified_name = 'T:Shop.Core.Services.PricingService';
      """));
  }

  [Fact]
  public async Task Refresh_does_not_load_msbuild()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    var filePath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");
    var text = await File.ReadAllTextAsync(filePath);

    var before = AppDomain.CurrentDomain.GetAssemblies()
      .Select(assembly => assembly.GetName().Name ?? string.Empty)
      .ToHashSet(StringComparer.Ordinal);

    var refresher = new SyntaxRefresher();
    refresher.Refresh(new FileRefreshRequest(fixture.RepoRoot, "src/Shop.Core/Services/PricingService.cs", text));

    var newlyLoaded = AppDomain.CurrentDomain.GetAssemblies()
      .Select(assembly => assembly.GetName().Name ?? string.Empty)
      .Where(name => !before.Contains(name))
      .ToArray();

    Assert.DoesNotContain(newlyLoaded, name => name.StartsWith("Microsoft.Build", StringComparison.Ordinal));
    Assert.DoesNotContain(newlyLoaded, name => name.StartsWith("Microsoft.CodeAnalysis.Workspaces", StringComparison.Ordinal));
  }

  private sealed class DemoShopFixture : IAsyncDisposable
  {
    private readonly string _root;
    private SqliteConnection? _connection;

    private DemoShopFixture(string root)
    {
      _root = root;
    }

    public string RepoRoot => _root;

    public static Task<DemoShopFixture> CreateAsync(Action<string>? mutate = null)
    {
      var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "DemoShop"));
      var destinationRoot = Path.Combine(Path.GetTempPath(), "waypoint-roslyn-tests", Guid.NewGuid().ToString("N"));
      CopyDirectory(sourceRoot, destinationRoot);
      Directory.CreateDirectory(Path.Combine(destinationRoot, ".git"));
      File.WriteAllText(Path.Combine(destinationRoot, ".git", "HEAD"), "ref: refs/heads/main");
      mutate?.Invoke(destinationRoot);
      return Task.FromResult(new DemoShopFixture(destinationRoot));
    }

    public async Task<IndexReport> IndexAsync()
    {
      var indexer = new SemanticIndexer();
      var report = await indexer.IndexAsync(new IndexOptions(_root)
      {
        SolutionPath = "DemoShop.slnx",
        Mode = IndexMode.MsBuild
      });

      _connection?.Dispose();
      _connection = WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      return report;
    }

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
      var connection = _connection ?? WaypointDb.Open(WaypointPaths.ForRepo(_root).DbPath);
      _connection ??= connection;
      using var command = connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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

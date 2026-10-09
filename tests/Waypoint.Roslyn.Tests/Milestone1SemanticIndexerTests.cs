using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using Waypoint.Roslyn.Semantic;
using Xunit;

namespace Waypoint.Roslyn.Tests;

public sealed class Milestone1SemanticIndexerTests
{
  [Fact]
  public async Task Index_DemoShop_loads_all_projects()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();

    var report = await fixture.IndexAsync();

    Assert.Equal(4, report.Projects);
    Assert.Equal(4L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE kind = 'project';"));
    Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE kind = 'project' AND json_extract(meta_json, '$.load_error') IS NOT NULL;"));
  }

  [Fact]
  public async Task Declarations_have_docids_and_spans()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    const string methodDocId = "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)";
    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = $q;", ("$q", methodDocId)));
    Assert.Equal(1L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM span s
      JOIN node n ON n.id = s.node_id
      WHERE n.qualified_name = $q
        AND s.file_path = 'src/Shop.Core/Services/PricingService.cs';
      """, ("$q", methodDocId)));
  }

  [Fact]
  public async Task Partial_class_has_two_spans()
  {
    await using var fixture = await DemoShopFixture.CreateAsync(repoRoot =>
    {
      var entityDirectory = Path.Combine(repoRoot, "src", "Shop.Core", "Entities");
      File.WriteAllText(
        Path.Combine(entityDirectory, "PartialThing.Part1.cs"),
        """
        namespace Shop.Core.Entities;

        public partial class PartialThing
        {
          public int A { get; set; }
        }
        """);
      File.WriteAllText(
        Path.Combine(entityDirectory, "PartialThing.Part2.cs"),
        """
        namespace Shop.Core.Entities;

        public partial class PartialThing
        {
          public int B { get; set; }
        }
        """);
    });

    await fixture.IndexAsync();

    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'T:Shop.Core.Entities.PartialThing';"));
    Assert.Equal(2L, fixture.Scalar("""
      SELECT COUNT(*)
      FROM span s
      JOIN node n ON n.id = s.node_id
      WHERE n.qualified_name = 'T:Shop.Core.Entities.PartialThing';
      """));
  }

  [Fact]
  public async Task Implements_injects_di_and_controller_routes_are_extracted()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    Assert.Equal(1L, fixture.EdgeCount("IMPLEMENTS", "T:Shop.Core.Services.PricingService", "T:Shop.Core.Interfaces.IPricingService"));
    Assert.Equal(1L, fixture.EdgeCount(
      "IMPLEMENTS",
      "M:Shop.Core.Services.PricingService.Apply(System.Decimal,System.Decimal)",
      "M:Shop.Core.Interfaces.IPricingService.Apply(System.Decimal,System.Decimal)"));
    Assert.Equal(1L, fixture.EdgeCount("INJECTS", "T:Shop.Core.Services.BasketService", "T:Shop.Core.Interfaces.IPricingService"));
    Assert.Equal(1L, fixture.EdgeCount("INJECTS", "T:Shop.Core.Services.PricingService", "T:Shop.Core.Interfaces.IShopContext"));
    Assert.Equal(1L, fixture.EdgeCount("REGISTERED_AS", "T:Shop.Core.Interfaces.IPricingService", "T:Shop.Core.Services.PricingService"));
    Assert.Equal(1L, fixture.EdgeCount("REGISTERED_AS", "T:Shop.Infrastructure.Data.ShopContext", "T:Shop.Infrastructure.Data.ShopContext"));
    Assert.Equal(1L, fixture.EdgeCount("REGISTERED_AS", "T:Shop.Core.Services.OrderService", "T:Shop.Core.Services.OrderService"));
    Assert.Equal("Scoped", fixture.StringScalar("""
      SELECT json_extract(meta_json, '$.lifetime')
      FROM node
      WHERE qualified_name = 'T:Shop.Core.Interfaces.IPricingService';
      """));
    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'ep:POST /api/basket/checkout';"));
    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'ep:GET /api/basket/{id}';"));
    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'ep:POST /api/orders';"));
  }

  [Fact]
  public async Task Reindex_skips_unchanged_files()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    var firstReport = await fixture.IndexAsync();
    var nodeCountBefore = fixture.Scalar("SELECT COUNT(*) FROM node;");
    var edgeCountBefore = fixture.Scalar("SELECT COUNT(*) FROM edge;");

    var secondReport = await fixture.IndexAsync();
    var nodeCountAfter = fixture.Scalar("SELECT COUNT(*) FROM node;");
    var edgeCountAfter = fixture.Scalar("SELECT COUNT(*) FROM edge;");

    Assert.True(firstReport.FilesSeen > 0);
    Assert.Equal(secondReport.FilesSeen, secondReport.FilesSkipped);
    Assert.Equal(nodeCountBefore, nodeCountAfter);
    Assert.Equal(edgeCountBefore, edgeCountAfter);
  }

  [Fact]
  public async Task Edited_file_is_reindexed()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    var pricingPath = Path.Combine(fixture.RepoRoot, "src", "Shop.Core", "Services", "PricingService.cs");

    await fixture.IndexAsync();
    File.AppendAllText(pricingPath, Environment.NewLine + "// touch");
    var report = await fixture.IndexAsync();

    Assert.True(report.FilesSeen > 0);
    Assert.Equal(report.FilesSeen - 1, report.FilesSkipped);
  }

  [Fact]
  public async Task Matches_expected_md_m1_facts()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    await fixture.IndexAsync();

    Assert.Equal(1L, fixture.EdgeCount("REGISTERED_AS", "T:Shop.Core.Interfaces.IPricingService", "T:Shop.Core.Services.PricingService"));
    Assert.Equal(1L, fixture.EdgeCount("INJECTS", "T:Shop.Core.Services.BasketService", "T:Shop.Core.Interfaces.IPricingService"));
    Assert.Equal(1L, fixture.EdgeCount("INJECTS", "T:Shop.Core.Services.OrderService", "T:Shop.Core.Interfaces.IPricingService"));
    Assert.Equal(1L, fixture.EdgeCount("INJECTS", "T:Shop.Core.Services.PricingService", "T:Shop.Core.Interfaces.IShopContext"));
    Assert.Equal(1L, fixture.EdgeCount("REGISTERED_AS", "T:Shop.Core.Interfaces.IShopContext", "T:Shop.Infrastructure.Data.ShopContextAdapter"));

    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'ep:POST /api/basket/checkout';"));
    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'ep:GET /api/basket/{id}';"));
    Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM node WHERE qualified_name = 'ep:POST /api/orders';"));
  }

  [Fact]
  public async Task Edge_counts_match_demo_shop_baseline()
  {
    await using var fixture = await DemoShopFixture.CreateAsync();
    var report = await fixture.IndexAsync();

    var expected = new Dictionary<string, int>(StringComparer.Ordinal)
    {
      ["CONTAINS"] = 80,
      ["IMPLEMENTS"] = 7,
      ["INJECTS"] = 7,
      ["REFERENCES"] = 5,
      ["REGISTERED_AS"] = 5,
      ["ROUTES_TO"] = 3
    };

    Assert.Equal(expected.Count, report.EdgesByKind.Count);
    foreach (var pair in expected)
    {
      Assert.True(report.EdgesByKind.TryGetValue(pair.Key, out var actual), $"Missing edge kind '{pair.Key}'.");
      Assert.Equal(pair.Value, actual);
    }
  }

  private sealed class DemoShopFixture : IAsyncDisposable
  {
    private readonly string _root;
    private Microsoft.Data.Sqlite.SqliteConnection? _connection;

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

    public long EdgeCount(string kind, string sourceQualifiedName, string destinationQualifiedName) =>
      Scalar("""
      SELECT COUNT(*)
      FROM edge e
      JOIN node src ON src.id = e.src
      JOIN node dst ON dst.id = e.dst
      WHERE e.kind = $kind
        AND src.qualified_name = $src
        AND dst.qualified_name = $dst;
      """,
      ("$kind", kind),
      ("$src", sourceQualifiedName),
      ("$dst", destinationQualifiedName));

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
      var connection = _connection ?? throw new InvalidOperationException("Call IndexAsync() before querying.");
      using var command = connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public string StringScalar(string sql, params (string Name, object Value)[] parameters)
    {
      var connection = _connection ?? throw new InvalidOperationException("Call IndexAsync() before querying.");
      using var command = connection.CreateCommand();
      command.CommandText = sql;
      foreach (var (name, value) in parameters)
      {
        command.Parameters.AddWithValue(name, value);
      }

      return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
        ?? string.Empty;
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

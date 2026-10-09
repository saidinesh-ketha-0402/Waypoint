using Waypoint.Core.Queries;
using Waypoint.Core.Storage;
using Xunit;

namespace Waypoint.Core.Tests;

public sealed class Milestone5ImpactQueryTests
{
  [Fact]
  public void ImpactQuery_returns_di_consumers_and_endpoints_for_interface()
  {
    var repo = TestRepo.CreateGitRepo();
    var dbPath = Paths.WaypointPaths.ForRepo(repo).DbPath;
    using var connection = WaypointDb.Open(dbPath);

    SeedImpactGraph(connection);
    var graph = new GraphStore(connection);
    var interfaceId = graph.NodeIdByQualifiedName("T:Shop.Core.Interfaces.IPricingService");
    Assert.True(interfaceId.HasValue);

    var impact = new ImpactQuery(connection).Run(interfaceId!.Value, depth: 2, throughDi: true);

    Assert.Contains(impact.Implementations, row => row.Implementation.Name == "PricingService");
    Assert.Equal("Scoped", impact.Implementations.Single(row => row.Implementation.Name == "PricingService").Lifetime);

    var consumerNames = impact.Consumers.Select(node => node.Name).ToArray();
    Assert.Contains("BasketService", consumerNames);
    Assert.Contains("OrderService", consumerNames);
    Assert.Contains("BasketController", consumerNames);
    Assert.Contains("OrdersController", consumerNames);

    var endpointNames = impact.Endpoints.Select(item => item.Endpoint.Name).ToArray();
    Assert.Contains("POST /api/basket/checkout", endpointNames);
    Assert.Contains("POST /api/orders", endpointNames);
  }

  [Fact]
  public void ImpactQuery_uses_di_endpoint_fallback_when_call_edges_are_missing()
  {
    var repo = TestRepo.CreateGitRepo();
    var dbPath = Paths.WaypointPaths.ForRepo(repo).DbPath;
    using var connection = WaypointDb.Open(dbPath);

    SeedImpactGraph(connection);
    var graph = new GraphStore(connection);
    var interfaceId = graph.NodeIdByQualifiedName("T:Shop.Core.Interfaces.IPricingService");
    Assert.True(interfaceId.HasValue);

    var impact = new ImpactQuery(connection).Run(interfaceId!.Value, depth: 2, throughDi: true);
    var formatter = new MarkdownFormatter();
    var markdown = formatter.RenderImpact("IPricingService", impact);

    Assert.Contains("POST /api/basket/checkout", markdown);
    Assert.Contains("POST /api/orders", markdown);
  }

  private static void SeedImpactGraph(Microsoft.Data.Sqlite.SqliteConnection connection)
  {
    var graph = new GraphStore(connection);

    graph.UpsertNode(new NodeUpsert("interface", "IPricingService", "T:Shop.Core.Interfaces.IPricingService", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("class", "PricingService", "T:Shop.Core.Services.PricingService", "Shop.Core", MetaJson: """{"lifetime":"Scoped"}"""));
    graph.UpsertNode(new NodeUpsert("class", "BasketService", "T:Shop.Core.Services.BasketService", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("class", "OrderService", "T:Shop.Core.Services.OrderService", "Shop.Core"));
    graph.UpsertNode(new NodeUpsert("class", "BasketController", "T:Shop.Web.Controllers.BasketController", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("class", "OrdersController", "T:Shop.Web.Controllers.OrdersController", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("method", "Checkout", "M:Shop.Web.Controllers.BasketController.Checkout()", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("method", "Create", "M:Shop.Web.Controllers.OrdersController.Create()", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("endpoint", "POST /api/basket/checkout", "ep:POST /api/basket/checkout", "Shop.Web"));
    graph.UpsertNode(new NodeUpsert("endpoint", "POST /api/orders", "ep:POST /api/orders", "Shop.Web"));

    graph.UpsertEdge(new EdgeUpsert("T:Shop.Core.Services.PricingService", "T:Shop.Core.Interfaces.IPricingService", "IMPLEMENTS"));
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
  }
}

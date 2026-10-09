using Shop.Core.Entities;
using Shop.Core.Interfaces;
using Shop.Core.Services;
using Xunit;

namespace Shop.UnitTests;

public sealed class PricingTests
{
  [Fact]
  public void Rounds_half_up()
  {
    var pricing = new PricingService(new FakeShopContext());
    var result = pricing.Apply(10.05m, 0m);
    Assert.Equal(10.05m, result);
  }

  [Fact]
  public void Clamps_to_50_percent()
  {
    var pricing = new PricingService(new FakeShopContext());
    var result = pricing.Apply(100m, 80m);
    Assert.Equal(50.00m, result);
  }

  private sealed class FakeShopContext : IShopContext
  {
    public Task<Basket?> FindBasketAsync(int basketId, CancellationToken cancellationToken = default) => Task.FromResult<Basket?>(null);
    public Task AddOrderAsync(Order order, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
  }
}

using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;
using Shop.Core.Interfaces;
using Shop.Core.Services;
using Shop.Infrastructure.Data;
using Xunit;

namespace Shop.UnitTests;

public sealed class BasketServiceTests
{
  [Fact]
  public async Task Checkout_applies_discount()
  {
    await using var context = new ShopContext(
      new DbContextOptionsBuilder<ShopContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString("N"))
        .Options);

    context.Baskets.Add(new Basket
    {
      Id = 42,
      Total = 200m,
      DiscountPct = 25m
    });
    await context.SaveChangesAsync();

    IShopContext adapter = new ShopContextAdapter(context);
    var pricing = new PricingService(adapter);
    var service = new BasketService(pricing, adapter);

    var order = await service.CheckoutAsync(42);

    Assert.Equal(150m, order.TotalCharged);
    Assert.Single(context.Orders);
  }
}

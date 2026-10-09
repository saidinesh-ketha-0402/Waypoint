using Shop.Core.Entities;
using Shop.Core.Interfaces;

namespace Shop.Core.Services;

public sealed class BasketService(IPricingService pricing, IShopContext db) : IBasketService
{
  private readonly IPricingService _pricing = pricing;
  private readonly IShopContext _db = db;

  public async Task<Order> CheckoutAsync(int basketId, CancellationToken cancellationToken = default)
  {
    var basket = await _db.FindBasketAsync(basketId, cancellationToken);
    if (basket is null)
    {
      throw new InvalidOperationException($"Basket {basketId} was not found.");
    }

    var charged = _pricing.Apply(basket.Total, basket.DiscountPct);
    var order = new Order
    {
      BasketId = basket.Id,
      TotalCharged = charged
    };

    await _db.AddOrderAsync(order, cancellationToken);
    await _db.SaveChangesAsync(cancellationToken);
    return order;
  }

  public Task<Basket?> GetAsync(int basketId, CancellationToken cancellationToken = default)
  {
    return _db.FindBasketAsync(basketId, cancellationToken);
  }
}

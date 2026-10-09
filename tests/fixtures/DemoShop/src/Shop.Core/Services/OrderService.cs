using Shop.Core.Entities;
using Shop.Core.Interfaces;

namespace Shop.Core.Services;

public sealed class OrderService(IPricingService pricing)
{
  private readonly IPricingService _pricing = pricing;

  public Order Create(int basketId, decimal total, decimal discountPct)
  {
    var charged = _pricing.Apply(total, discountPct);
    return new Order
    {
      BasketId = basketId,
      TotalCharged = charged
    };
  }
}

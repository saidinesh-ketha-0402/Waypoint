using Shop.Core.Entities;
using Shop.Core.Interfaces;

namespace Shop.Core.Services;

public sealed class PricingService(IShopContext db) : IPricingService
{
  private readonly IShopContext _db = db;

  public decimal Apply(decimal total, decimal discountPct)
  {
    _ = _db;
    var clampedDiscount = Math.Clamp(discountPct, 0m, DiscountPolicy.MaxDiscountPct);
    var multiplier = 1m - clampedDiscount / 100m;
    return decimal.Round(total * multiplier, 2, MidpointRounding.AwayFromZero);
  }
}

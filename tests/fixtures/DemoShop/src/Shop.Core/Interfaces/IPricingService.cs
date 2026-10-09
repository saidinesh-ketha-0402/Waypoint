namespace Shop.Core.Interfaces;

public interface IPricingService
{
  decimal Apply(decimal total, decimal discountPct);
}

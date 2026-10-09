namespace Shop.Core.Entities;

public sealed class Basket
{
  public int Id { get; set; }
  public decimal Total { get; set; }
  public decimal DiscountPct { get; set; }
}

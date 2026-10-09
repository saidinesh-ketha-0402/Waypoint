namespace Shop.Core.Entities;

public sealed class Order
{
  public int Id { get; set; }
  public int BasketId { get; set; }
  public decimal TotalCharged { get; set; }
}

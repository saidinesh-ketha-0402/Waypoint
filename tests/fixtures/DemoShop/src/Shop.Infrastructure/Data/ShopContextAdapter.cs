using Shop.Core.Entities;
using Shop.Core.Interfaces;

namespace Shop.Infrastructure.Data;

public sealed class ShopContextAdapter(ShopContext db) : IShopContext
{
  private readonly ShopContext _db = db;

  public Task<Basket?> FindBasketAsync(int basketId, CancellationToken cancellationToken = default)
  {
    return _db.Baskets.FindAsync([basketId], cancellationToken).AsTask();
  }

  public Task AddOrderAsync(Order order, CancellationToken cancellationToken = default)
  {
    return _db.Orders.AddAsync(order, cancellationToken).AsTask();
  }

  public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
  {
    return _db.SaveChangesAsync(cancellationToken);
  }
}

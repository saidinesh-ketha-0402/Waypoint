using Shop.Core.Entities;

namespace Shop.Core.Interfaces;

public interface IShopContext
{
  Task<Basket?> FindBasketAsync(int basketId, CancellationToken cancellationToken = default);
  Task AddOrderAsync(Order order, CancellationToken cancellationToken = default);
  Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

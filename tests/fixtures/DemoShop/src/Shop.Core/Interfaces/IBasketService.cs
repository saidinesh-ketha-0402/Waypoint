using Shop.Core.Entities;

namespace Shop.Core.Interfaces;

public interface IBasketService
{
  Task<Order> CheckoutAsync(int basketId, CancellationToken cancellationToken = default);
  Task<Basket?> GetAsync(int basketId, CancellationToken cancellationToken = default);
}

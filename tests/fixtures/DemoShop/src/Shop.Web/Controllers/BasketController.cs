using Microsoft.AspNetCore.Mvc;
using Shop.Core.Interfaces;

namespace Shop.Web.Controllers;

[ApiController]
[Route("api/basket")]
public sealed class BasketController(IBasketService baskets) : ControllerBase
{
  private readonly IBasketService _baskets = baskets;

  [HttpPost("checkout")]
  public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request, CancellationToken cancellationToken)
  {
    var order = await _baskets.CheckoutAsync(request.BasketId, cancellationToken);
    return Ok(order);
  }

  [HttpGet("{id:int}")]
  public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
  {
    var basket = await _baskets.GetAsync(id, cancellationToken);
    return basket is null ? NotFound() : Ok(basket);
  }

  public sealed record CheckoutRequest(int BasketId);
}

using Microsoft.AspNetCore.Mvc;
using Shop.Core.Services;

namespace Shop.Web.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(OrderService orders) : ControllerBase
{
  private readonly OrderService _orders = orders;

  [HttpPost]
  public IActionResult Create([FromBody] CreateOrderRequest request)
  {
    var order = _orders.Create(request.BasketId, request.Total, request.DiscountPct);
    return Ok(order);
  }

  public sealed record CreateOrderRequest(int BasketId, decimal Total, decimal DiscountPct);
}

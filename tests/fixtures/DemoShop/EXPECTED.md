# DemoShop Expected KG Facts

- `IPricingService` is registered exactly once in `Program.cs` as `AddScoped<IPricingService, PricingService>()`.
- `IPricingService` has two constructor consumers: `BasketService` and `OrderService`.
- `PricingService` depends on scoped `IShopContext`, which is backed by `ShopContextAdapter` over `ShopContext`.
- `BasketController` exposes `POST /api/basket/checkout` and `GET /api/basket/{id}`.
- `OrdersController` exposes `POST /api/orders`.
- `Program.cs` exposes `GET /api/health` returning `"ok"`.

# Demo Edits For Later Tests

- Change `PricingService.Apply` rounding mode from `MidpointRounding.AwayFromZero` to `MidpointRounding.ToEven`.
- Change DI in `Program.cs` to `AddSingleton<IPricingService, PricingService>()` to trigger scoped-into-singleton startup failure.
- Add a `BasketController` endpoint such as `POST /api/basket/discount` for graph/index update tests.

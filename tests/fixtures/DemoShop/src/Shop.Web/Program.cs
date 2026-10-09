using Microsoft.EntityFrameworkCore;
using Shop.Core.Interfaces;
using Shop.Core.Services;
using Shop.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<ShopContext>(options => options.UseInMemoryDatabase("DemoShop"));
builder.Services.AddScoped<IShopContext>(sp => new ShopContextAdapter(sp.GetRequiredService<ShopContext>()));
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IBasketService, BasketService>();
builder.Services.AddScoped<OrderService>();

var app = builder.Build();

app.MapControllers();
app.MapGet("/api/health", () => "ok");

app.Run();

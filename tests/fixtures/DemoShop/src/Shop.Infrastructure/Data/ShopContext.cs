using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data;

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
  public DbSet<Basket> Baskets => Set<Basket>();
  public DbSet<Order> Orders => Set<Order>();
}

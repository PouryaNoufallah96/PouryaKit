using Microsoft.EntityFrameworkCore;
using PouryaKit.Persistence.EFCore;

namespace Inventory.Infrastructure.Data.Context;

internal sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : PouryaKitDbContext(options);

using Microsoft.EntityFrameworkCore;
using PouryaKit.Persistence.EFCore;

namespace Catalog.Infrastructure.Data.Context;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : PouryaKitDbContext(options);

using Microsoft.EntityFrameworkCore;
using PouryaKit.Persistence.EFCore;

namespace Billing.Infrastructure.Data.Context;

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : PouryaKitDbContext(options);

using Microsoft.EntityFrameworkCore;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Reporting.Module;

internal sealed class ReportDbContext(DbContextOptions<ReportDbContext> options)
    : PouryaKitDbContext(options);

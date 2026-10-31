using Microsoft.EntityFrameworkCore;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Stub;

internal sealed class StubDbContext(DbContextOptions<StubDbContext> options)
    : PouryaKitDbContext(options);

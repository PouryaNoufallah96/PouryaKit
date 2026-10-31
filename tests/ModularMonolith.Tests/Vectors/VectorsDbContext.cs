using Microsoft.EntityFrameworkCore;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Vectors;

/// <summary>The Module's DbContext: no aggregate, only the migration that creates its vector collection.</summary>
public sealed class VectorsDbContext(DbContextOptions<VectorsDbContext> options)
    : PouryaKitDbContext(options);

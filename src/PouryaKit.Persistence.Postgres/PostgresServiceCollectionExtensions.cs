using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Persistence.Postgres;

public static class PostgresServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Stores the Module's data with <typeparamref name="TDbContext"/> in PostgreSQL: its own <paramref name="schema"/>
        /// with its own migrations history table, snake_case names, and the connection string named
        /// <paramref name="connectionName"/> in the Host's configuration. The DbContext becomes the Module's Unit of
        /// Work. No retrying execution strategy is configured: the Unit of Work owns its transaction, and a retry needs
        /// a new scope.
        /// </summary>
        public IServiceCollection UsePostgres<TDbContext>(string connectionName, string schema)
            where TDbContext : PouryaKitDbContext
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);

            services.AddDbContext<TDbContext>(options =>
                options
                    .UseNpgsql(
                        $"Name=ConnectionStrings:{connectionName}",
                        npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema)
                    )
                    .UseSnakeCaseNamingConvention()
            );
            return services.AddPouryaKitEfCore<TDbContext>();
        }
    }
}

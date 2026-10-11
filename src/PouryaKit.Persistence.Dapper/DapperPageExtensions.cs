using System.Data.Common;
using Dapper;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Dapper;

/// <summary>
/// Reads one Page of a Feature's SQL through Dapper on a <see cref="DbConnection"/>, part of the Dapper reads
/// Capability.
/// </summary>
public static class DapperPageExtensions
{
    extension(DbConnection connection)
    {
        /// <summary>
        /// Reads one Page on this connection, usually the lease from <see cref="IReadConnectionFactory"/>: the
        /// <see cref="PageSql.Count"/> of the filtered selection, then the <see cref="PageSql.Select"/> ordered by the
        /// column mapped to the request's sort key, then the <see cref="PageSql.ThenBy"/> column, and limited to the
        /// Page. Every filter value, the size and the offset are parameters, beside the Feature's own
        /// <paramref name="parameters"/>. A Page beyond the last has no items and the true total. Throws an
        /// <see cref="InvalidOperationException"/>, before any SQL runs, when <paramref name="request"/> fails
        /// <see cref="PageDeclaration.Validate"/>: the query's validator should have refused it.
        /// </summary>
        public async Task<Page<T>> QueryPageAsync<T>(
            PageSql sql,
            PageRequest request,
            object? parameters = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(sql);
            ArgumentNullException.ThrowIfNull(request);
            if (sql.Declaration.Validate(request) is { IsFailure: true } invalid)
            {
                throw new InvalidOperationException(
                    $"The Page request fails its declaration ({string.Join(", ", invalid.Errors.Select(error => error.Code))}); check it with PageDeclaration.Validate in the query's validator."
                );
            }

            var select = sql.SelectFor(request);
            var bound = sql.ParametersFor(request, parameters);
            var total = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(sql.Count, bound, cancellationToken: cancellationToken)
            );
            var items = await connection.QueryAsync<T>(
                new CommandDefinition(select, bound, cancellationToken: cancellationToken)
            );
            return new Page<T>(items.AsList(), request.Number, request.Size, total);
        }
    }
}

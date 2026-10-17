using System.Data.Common;

namespace PouryaKit.Persistence.Dapper;

/// <summary>
/// Opens connections for Dapper reads. Each call leases a fresh connection from the Host's pool; the caller owns it
/// for the length of the query and disposes it, which returns it to the pool:
/// <c>await using var connection = await connections.OpenAsync(cancellationToken);</c>
/// A connection from here never joins the Operation's Unit of Work, so it is for reads only. Each lease, as it opens,
/// sets the PostgreSQL session setting <c>pouryakit.tenant</c> to the tenant of the scope's Operation, or to the empty
/// string, which no tenant is, where the Operation has none or none began; a read of a tenant-scoped table compares
/// <c>tenant = current_setting('pouryakit.tenant')</c> rather than binding a tenant of its own. Npgsql resets a
/// connection's session state when it returns to the pool (<c>DISCARD ALL</c>, unless the connection string sets
/// <c>No Reset On Close</c>), and the next lease sets its own tenant either way. A connection string that sets
/// <c>Multiplexing=true</c> is refused, since a multiplexed command does not keep its lease's session.
/// </summary>
public interface IReadConnectionFactory
{
    /// <summary>A new open connection, which the caller disposes.</summary>
    ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

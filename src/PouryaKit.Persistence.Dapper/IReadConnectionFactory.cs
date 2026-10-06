using System.Data.Common;

namespace PouryaKit.Persistence.Dapper;

/// <summary>
/// Opens connections for Dapper reads. Each call leases a fresh connection from the Host's pool; the caller owns it
/// for the length of the query and disposes it, which returns it to the pool:
/// <c>await using var connection = await connections.OpenAsync(cancellationToken);</c>
/// A connection from here never joins the Operation's Unit of Work, so it is for reads only.
/// </summary>
public interface IReadConnectionFactory
{
    /// <summary>A new open connection, which the caller disposes.</summary>
    ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

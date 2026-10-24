using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Marten;

/// <summary>
/// Runs a Kit statement in the transaction of a Marten Unit of Work, for a Kit package that writes there, such as a
/// Notification Handler's Receipt. A static accessor rather than a member of <see cref="IUnitOfWork"/> or
/// <see cref="IStreams"/>, so Feature code is never offered it; a structure test refuses a Module that calls it.
/// </summary>
public static class MartenUnitOfWorkTransaction
{
    /// <summary>
    /// Runs <paramref name="statement"/> over the table <paramref name="table"/> of the Module's schema on the
    /// transaction of <paramref name="unitOfWork"/>, which the Unit of Work began before the command, its composite-format
    /// items bound to <paramref name="values"/> in order, and returns the rows it affected. Throws when
    /// <paramref name="unitOfWork"/> is not a Marten Unit of Work, or none is active.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        IUnitOfWork unitOfWork,
        Func<string, string> statement,
        string table,
        object[] values,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(values);
        await using var command = Of(unitOfWork).Command(statement, table, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static MartenUnitOfWork Of(IUnitOfWork unitOfWork) =>
        unitOfWork as MartenUnitOfWork
        ?? throw new InvalidOperationException(
            $"This is kept in the Marten Unit of Work's transaction, but the scope's Unit of Work is {unitOfWork?.GetType().Name ?? "none"}."
        );
}

using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>The Errors the dispatcher fails a rolled-back Operation with.</summary>
public static class OperationErrors
{
    /// <summary>
    /// A command inside the Operation threw, so the Operation was rolled back, even when the code that sent the command
    /// handled the exception. The Error carries no exception text.
    /// </summary>
    public static readonly Error CommandThrew = Error.Unexpected(
        "Operation.CommandThrew",
        "A command failed with an exception, so the Operation was rolled back."
    );

    /// <summary>
    /// Code that only reads, such as a query, an authorizer, a validator or a step opened with
    /// <see cref="Operation.OnlyReading(string)"/>, changed tracked state, so the Operation was rolled back.
    /// </summary>
    public static readonly Error ReadingChangedState = Error.Unexpected(
        "Operation.ReadingChangedState",
        "Code that only reads changed tracked state, so the Operation was rolled back."
    );
}

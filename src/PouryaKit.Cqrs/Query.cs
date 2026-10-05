namespace PouryaKit.Cqrs;

/// <summary>
/// A question about state that yields a <typeparamref name="TResponse"/> on success. Declare each as a sealed record
/// deriving from it, with exactly one <see cref="QueryHandler{TQuery, TResponse}"/>. A query never runs in a Unit of
/// Work, so its handler may not send a command.
/// </summary>
public abstract record Query<TResponse>
    where TResponse : notnull;

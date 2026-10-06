namespace PouryaKit.Cqrs;

/// <summary>
/// A question about state that yields a <typeparamref name="TResponse"/> on success. Declare each as a sealed record
/// deriving from it, with exactly one <see cref="QueryHandler{TQuery, TResponse}"/>. A query does not start or own a
/// Unit of Work, but may run inside a command's active one. Its handler may not send a command.
/// </summary>
public abstract record Query<TResponse>
    where TResponse : notnull;

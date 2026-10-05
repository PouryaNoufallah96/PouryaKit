namespace PouryaKit.Cqrs;

/// <summary>
/// A request to change state that yields no value. Declare each as a sealed record deriving from it, with exactly
/// one <see cref="CommandHandler{TCommand}"/>.
/// </summary>
public abstract record Command;

/// <summary>
/// A request to change state that yields a <typeparamref name="TResponse"/> on success. Declare each as a sealed
/// record deriving from it, with exactly one <see cref="CommandHandler{TCommand, TResponse}"/>.
/// </summary>
public abstract record Command<TResponse>
    where TResponse : notnull;

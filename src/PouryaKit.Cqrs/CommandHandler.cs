using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>The one handler of a <typeparamref name="TCommand"/>. It returns a Result and never saves or commits.</summary>
public abstract class CommandHandler<TCommand>
    where TCommand : Command
{
    /// <summary>Runs the business behaviour of <paramref name="command"/> and returns its Result.</summary>
    public abstract Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>The one handler of a <typeparamref name="TCommand"/>. It returns a Result and never saves or commits.</summary>
public abstract class CommandHandler<TCommand, TResponse>
    where TCommand : Command<TResponse>
    where TResponse : notnull
{
    /// <summary>
    /// Runs the business behaviour of <paramref name="command"/> and returns its Result, which holds the response on
    /// success.
    /// </summary>
    public abstract Task<Result<TResponse>> Handle(
        TCommand command,
        CancellationToken cancellationToken
    );
}

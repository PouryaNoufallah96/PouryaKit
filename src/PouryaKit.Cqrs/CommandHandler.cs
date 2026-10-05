using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>The one handler of a <typeparamref name="TCommand"/>. It returns a Result and never saves or commits.</summary>
public abstract class CommandHandler<TCommand>
    where TCommand : Command
{
    public abstract Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>The one handler of a <typeparamref name="TCommand"/>. It returns a Result and never saves or commits.</summary>
public abstract class CommandHandler<TCommand, TResponse>
    where TCommand : Command<TResponse>
    where TResponse : notnull
{
    public abstract Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}

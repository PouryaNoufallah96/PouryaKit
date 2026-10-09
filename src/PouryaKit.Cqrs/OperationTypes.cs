namespace PouryaKit.Cqrs;

/// <summary>
/// The one rule by which the Kit tells commands, queries and their handlers apart, for a Kit package's registration or
/// composition check: a command derives from <see cref="Command"/> or <see cref="Command{TResponse}"/>, a query from
/// <see cref="Query{TResponse}"/>, directly or through a base class of its own.
/// </summary>
public static class OperationTypes
{
    /// <summary>True when <paramref name="type"/> is a command or a query.</summary>
    public static bool IsOperation(Type type) => DerivesFrom(type, query: true);

    /// <summary>True when <paramref name="type"/> is a command, with or without a response.</summary>
    public static bool IsCommand(Type type) => DerivesFrom(type, query: false);

    /// <summary>
    /// The command or query <paramref name="handlerService"/> handles, when it is exactly the service a handler is
    /// registered as and the dispatcher resolves: <see cref="CommandHandler{TCommand}"/>,
    /// <see cref="CommandHandler{TCommand, TResponse}"/> or <see cref="QueryHandler{TQuery, TResponse}"/> of it; null
    /// for any other type, a handler's own class included.
    /// </summary>
    public static Type? HandledBy(Type handlerService)
    {
        ArgumentNullException.ThrowIfNull(handlerService);
        return handlerService.IsConstructedGenericType
            && handlerService.GetGenericTypeDefinition() is var definition
            && (definition == typeof(CommandHandler<>) || definition == typeof(CommandHandler<,>) || definition == typeof(QueryHandler<,>))
                ? handlerService.GenericTypeArguments[0]
                : null;
    }

    private static bool DerivesFrom(Type type, bool query)
    {
        ArgumentNullException.ThrowIfNull(type);
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (
                current == typeof(Command)
                || (
                    current.IsGenericType
                    && current.GetGenericTypeDefinition() is var definition
                    && (definition == typeof(Command<>) || (query && definition == typeof(Query<>)))
                )
            )
            {
                return true;
            }
        }

        return false;
    }
}

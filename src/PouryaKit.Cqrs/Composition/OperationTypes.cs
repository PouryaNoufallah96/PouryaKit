using System.Collections.Concurrent;

namespace PouryaKit.Cqrs;

/// <summary>
/// The one rule by which the Kit tells commands, queries and their handlers apart, for a Kit package's registration or
/// composition check: a command derives from <see cref="Command"/> or <see cref="Command{TResponse}"/>, a query from
/// <see cref="Query{TResponse}"/>, directly or through a base class of its own. Also the one name a Kit package's key
/// gives a command or a query (<see cref="KeyName"/>).
/// </summary>
public static class OperationTypes
{
    private static readonly ConcurrentDictionary<Type, string> KeyNames = new();

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

    /// <summary>
    /// The name a stored or shared key gives <paramref name="operation"/>, such as the idempotency key's command and the
    /// query cache key's query: its full name, then its assembly's simple name (<c>Namespace.Type, Assembly</c>). Two
    /// assemblies may hold a type of one full name, and each keeps keys of its own; it is never the assembly-qualified
    /// name, whose version would change every key at a deploy. Read once per type.
    /// </summary>
    public static string KeyName(Type operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return KeyNames.GetOrAdd(operation, static type => $"{type.FullName}, {type.Assembly.GetName().Name}");
    }

    /// <summary>
    /// Every command and query that registering <paramref name="types"/>, concrete types as
    /// <see cref="PouryaKit.Core.ImplementingTypes.Of"/> returns them, makes dispatchable: the commands and queries among them, and each
    /// one a handler among them handles, wherever it is declared, such as in a Module's Contracts. Each once. A Kit
    /// package's registration of a check on every command and query of the Features reads this set, so the check covers
    /// what the dispatcher runs.
    /// </summary>
    public static IEnumerable<Type> Dispatchable(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return types.Where(IsOperation).Concat(types.Select(HandlerBase).OfType<Type>().Select(handler => handler.GenericTypeArguments[0])).Distinct();
    }

    // The handler service a handler class is registered as: the first base class that is one, or null.
    internal static Type? HandlerBase(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (HandledBy(current) is not null)
            {
                return current;
            }
        }

        return null;
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

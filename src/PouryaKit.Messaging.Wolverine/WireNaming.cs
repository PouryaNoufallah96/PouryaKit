using System.Collections.Concurrent;
using PouryaKit.Domain;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

// Wolverine's naming strategies, and the names they produce, are process-wide. A Domain Event's wire name is a pure
// function of its type. An Integration Event's is declared by the Module that publishes it, or by a Host that receives it
// from another, so it is kept here for the whole process, and a second Host in the process that declares the type under
// another name is refused: Wolverine would otherwise go on using the first name.
internal sealed class WireNaming : IMessageTypeNaming
{
    private static readonly ConcurrentDictionary<Type, string> Declared = new();
    private static readonly Lock Declaring = new();

    /// <summary>
    /// Records the wire name of every Integration Event and Snapshot in <paramref name="wireNames"/>, published by the
    /// Host or, for the types in <paramref name="received"/>, received from another Host, for this process, all or none,
    /// and returns a problem for each one this process already knows under another name, or that Wolverine named before it
    /// was declared: Wolverine keeps the first name it gives a type, and its envelopes would carry that one.
    /// </summary>
    public static List<string> Declare(IReadOnlyDictionary<Type, string> wireNames, IReadOnlySet<Type> received) =>
        Declare(wireNames, (type, wireName) => $"{type.FullName} is {(received.Contains(type) ? "received" : "published")} as {wireName}");

    /// <summary>
    /// Records the name of each Saga's input type in <paramref name="names"/> (<c>PouryaKit.Messaging.Wolverine.Sagas</c>),
    /// its stored envelopes' message type, for this process, all or none, with the problems of <c>Declare</c>.
    /// </summary>
    public static List<string> DeclareSagaInputs(IReadOnlyDictionary<Type, string> names) =>
        Declare(names, (type, name) => $"The Saga input {type.FullName} is named {name}");

    private static List<string> Declare(IReadOnlyDictionary<Type, string> wireNames, Func<Type, string, string> describe)
    {
        lock (Declaring)
        {
            List<string> problems =
            [
                .. wireNames
                    .Where(named => Declared.TryGetValue(named.Key, out var name) && name != named.Value)
                    .Select(named =>
                        $"{describe(named.Key, named.Value)}, but this process already sends it as {Declared[named.Key]}; a type keeps one wire name in a process."
                    ),
            ];
            if (problems.Count > 0)
            {
                return problems;
            }

            foreach (var (type, wireName) in wireNames)
            {
                Declared.TryAdd(type, wireName);
            }

            return
            [
                .. wireNames
                    .Select(named => (named.Key, Declared: named.Value, Sent: named.Key.ToMessageTypeName()))
                    .Where(named => named.Sent != named.Declared)
                    .Select(named =>
                        $"{describe(named.Key, named.Declared)}, but Wolverine named it {named.Sent} before it was declared and would send it under that name. Declare it before anything in the process sends or names it."
                    ),
            ];
        }
    }

    /// <summary>
    /// True when this process has declared each of <paramref name="wireNames"/> as its type's name, so naming the types
    /// can fix no other: a check that resolves Wolverine's options, which names every routed type, reads it first.
    /// </summary>
    public static bool AllDeclared(IReadOnlyDictionary<Type, string> wireNames) =>
        wireNames.All(named => Declared.TryGetValue(named.Key, out var name) && name == named.Value);

    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        messageTypeName = Declared.TryGetValue(messageType, out var declared)
            ? declared
            : messageType.IsAssignableTo(typeof(DomainEvent)) && !messageType.IsAbstract
                ? MessageNames.WireNameOf(messageType)
                : "";
        return MessageNames.IsValid(messageTypeName);
    }
}

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
    /// Records the wire name of every Integration Event and Snapshot in <paramref name="wireNames"/> for this process,
    /// all or none, and returns a problem for each one this process already knows under another name, or that Wolverine
    /// named before it was declared: Wolverine keeps the first name it gives a type, and its envelopes would carry that one.
    /// </summary>
    public static List<string> Declare(IReadOnlyDictionary<Type, string> wireNames)
    {
        lock (Declaring)
        {
            List<string> problems =
            [
                .. wireNames
                    .Where(named => Declared.TryGetValue(named.Key, out var name) && name != named.Value)
                    .Select(named =>
                        $"{named.Key.FullName} is published as {named.Value}, but this process already sends it as {Declared[named.Key]}; a type keeps one wire name in a process."
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
                        $"{named.Key.FullName} is published as {named.Declared}, but Wolverine named it {named.Sent} before it was declared and would send it under that name. Declare it before anything in the process sends or names it."
                    ),
            ];
        }
    }

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

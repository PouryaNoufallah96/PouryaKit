using System.Collections.Concurrent;
using PouryaKit.Domain;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

// Wolverine's naming strategies, and the names they produce, are process-wide. A Domain Event's wire name is a pure
// function of its type. An Integration Event's is declared by the Module that publishes it, so it is kept here for the
// whole process, and a second Host in the process that declares the type under another name is refused: Wolverine would
// otherwise go on using the first name.
internal sealed class WireNaming : IMessageTypeNaming
{
    private static readonly ConcurrentDictionary<Type, string> Declared = new();

    /// <summary>
    /// Records the wire name of every Integration Event in <paramref name="publications"/> for this process, and returns
    /// a problem for each one this process already knows under another name.
    /// </summary>
    public static IEnumerable<string> Declare(IReadOnlyDictionary<Type, Publication> publications)
    {
        foreach (var (type, publication) in publications)
        {
            var name = Declared.GetOrAdd(type, publication.WireName);
            if (name != publication.WireName)
            {
                yield return $"{type.FullName} is published as {publication.WireName}, but this process already sends it as {name}; a type keeps one wire name in a process.";
            }
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

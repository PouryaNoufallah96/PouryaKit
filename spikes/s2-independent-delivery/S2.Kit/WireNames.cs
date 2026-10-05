using System.Collections.Concurrent;
using Wolverine.Util;

namespace S2.Kit;

/// <summary>
/// Stable wire names independent of CLR type names, declared by Modules with no transport type. Wolverine's naming
/// strategies are process-global statics, so this registry is too.
/// </summary>
public static class WireNames
{
    private static readonly ConcurrentDictionary<Type, string> Names = new();

    public static void Register(Type messageType, string wireName)
    {
        if (Names.GetOrAdd(messageType, wireName) != wireName)
            throw new InvalidOperationException($"{messageType} already has wire name {Names[messageType]}");
    }

    public static string Of(Type messageType) => Names[messageType];

    public static void UseInWolverine() => WolverineMessageNaming.InsertFirst<Naming>();

    public sealed class Naming : IMessageTypeNaming
    {
        public bool TryDetermineName(Type messageType, out string messageTypeName) =>
            Names.TryGetValue(messageType, out messageTypeName!);
    }
}

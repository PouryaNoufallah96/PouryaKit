namespace PouryaKit.Core.Tests.Lookup;

// The implementing-type lookup reads every type of this assembly; only these declare its contracts.

internal interface IHandler<T>;

internal interface IAudited;

internal sealed class NoteHandler : IHandler<string>, IAudited;

internal abstract class AbstractHandler : IHandler<int>, IAudited;

internal sealed class DerivedHandler : AbstractHandler;

internal sealed class OpenHandler<T> : IHandler<T>, IAudited;

internal interface ISpecialHandler : IHandler<long>;

internal struct ValueHandler : IHandler<byte>;

internal abstract class Store<T>;

internal sealed class Ledger : Store<decimal>;

internal abstract class AbstractStore : Store<long>;

// The compiler turns the iterator into a class of its own that implements IEnumerable<NoteHandler>.
internal static class NoteHandlers
{
    public static IEnumerable<NoteHandler> All()
    {
        yield return new NoteHandler();
    }
}

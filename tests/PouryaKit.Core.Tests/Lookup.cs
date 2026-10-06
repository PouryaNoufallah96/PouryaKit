namespace PouryaKit.Core.Tests.Lookup;

// The implementing-type lookup reads every type of this assembly; only these declare its contracts.

internal interface IHandler<T>;

internal interface IAudited;

internal sealed class NoteHandler : IHandler<string>, IAudited;

internal abstract class AbstractHandler : IHandler<int>, IAudited;

internal sealed class DerivedHandler : AbstractHandler;

internal sealed class OpenHandler<T> : IHandler<T>;

internal interface ISpecialHandler : IHandler<long>;

internal struct ValueHandler : IHandler<byte>;

internal abstract class Store<T>;

internal sealed class Ledger : Store<decimal>;

internal abstract class AbstractStore : Store<long>;

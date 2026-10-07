namespace PouryaKit.Cqrs;

/// <summary>
/// Opts a command into request idempotency. When it runs as the outermost command of an Operation whose
/// <see cref="OperationContext.RequestId"/> is set, the dispatcher reserves its key, the tenant, Actor, command and
/// request id, inside the command's Unit of Work transaction before the handler runs, and records its success in that
/// transaction. A repeat with the same key replays that success, the same Result value, without running the handler, so
/// nothing the first run wrote or published happens twice. A concurrent repeat waits until the first Operation ends,
/// then replays its committed success, or runs if it rolled back. A failed or rolled-back Operation records nothing, so
/// a repeat runs again. A command nested in another never reserves. A request id reaching an outermost command that
/// does not opt in, or a Host without an <see cref="IIdempotencyStore"/>, fails the dispatch with an exception.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class IdempotentAttribute : Attribute;

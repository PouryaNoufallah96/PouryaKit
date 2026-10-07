using PouryaKit.Cqrs;

// The packed request-idempotency contract: a command that opts in, a context that carries a request id, and a store
// as a persistence package implements it; no database is needed to build it.
var context = new OperationContext(Actor.Person("package-consumer")) { RequestId = "request-1" };
IIdempotencyStore store = new NoKeys();
var recorded = await store.ReserveAsync(
    new IdempotencyKey(context.Tenant, context.Actor, typeof(PlaceOrderCommand).FullName!, context.RequestId!),
    CancellationToken.None
);
Console.WriteLine(recorded is null && context.Continue().RequestId is null ? 1 : 0);

[Idempotent]
internal sealed record PlaceOrderCommand(Guid OrderId) : Command<Guid>;

internal sealed class NoKeys : IIdempotencyStore
{
    public Task<RecordedSuccess?> ReserveAsync(IdempotencyKey key, CancellationToken cancellationToken) =>
        Task.FromResult<RecordedSuccess?>(null);

    public Task RecordAsync(IdempotencyKey key, string response, CancellationToken cancellationToken) => Task.CompletedTask;
}

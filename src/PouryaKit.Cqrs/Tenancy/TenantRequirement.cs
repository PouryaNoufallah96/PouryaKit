using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

// The tenant requirement of one command or query of a tenant-scoped Module or application, registered by
// AddPouryaKitTenancy. It reads the Operation, not its context, so a scope nobody began is refused like any other
// Operation without a tenant. It runs for a trusted continuation too, the one exception spec #115 makes to G.6: no
// authority an earlier Operation held lets work run under no tenant where every row belongs to one.
internal sealed class TenantRequirement<TOperation>(Operation running) : IAuthorizer<TOperation>
    where TOperation : notnull
{
    private static readonly Task<Result> Allowed = Task.FromResult(Result.Success());
    private static readonly Task<Result> Refused = Task.FromResult<Result>(TenantErrors.Required);

    public Task<Result> AuthorizeAsync(TOperation operation, CancellationToken cancellationToken) =>
        running.Context?.Tenant is null ? Refused : Allowed;
}

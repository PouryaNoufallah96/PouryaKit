using System.Reflection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Security;

// The permission check of one command or query that declares a permission, registered by AddPouryaKitPermissions.
internal sealed class PermissionAuthorizer<TOperation>(OperationContext context, IActorPermissions permissions)
    : IAuthorizer<TOperation>
    where TOperation : notnull
{
    private static readonly string Permission =
        typeof(TOperation).GetCustomAttribute<RequiresPermissionAttribute>()!.Permission;

    public async Task<Result> AuthorizeAsync(TOperation operation, CancellationToken cancellationToken)
    {
        // A continuation the Kit restored runs on the authority of the authorized Operation that caused it (G.6, ADR
        // 0007): its Actor is not authorized again, so durable work never depends on what that Actor may do by the time
        // it runs. A causation alone proves nothing: application code can give any.
        if (context.IsTrustedContinuation)
        {
            return Result.Success();
        }

        return await permissions.HasPermissionAsync(context, Permission, cancellationToken)
            ? Result.Success()
            : PermissionErrors.Denied;
    }
}

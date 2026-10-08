using PouryaKit.Cqrs;

namespace PouryaKit.Security;

/// <summary>
/// The application's source of the permissions an Actor holds. The Kit has no role system or identity store: the
/// application implements this from its own source, such as the permission claims its authentication scheme issues,
/// and registers it. It is asked for a command or query that declares a permission
/// (<see cref="RequiresPermissionAttribute"/>), unless the Operation is a trusted continuation, one whose context is
/// marked <see cref="OperationContext.IsTrustedContinuation"/>; a causation alone does not make one.
/// </summary>
public interface IActorPermissions
{
    /// <summary>Whether the Actor <paramref name="context"/> runs for, in its tenant, holds <paramref name="permission"/>.</summary>
    Task<bool> HasPermissionAsync(OperationContext context, string permission, CancellationToken cancellationToken);
}

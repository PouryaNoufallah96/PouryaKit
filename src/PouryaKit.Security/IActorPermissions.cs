using PouryaKit.Cqrs;

namespace PouryaKit.Security;

/// <summary>
/// The application's source of the permissions an Actor holds. The Kit has no role system or identity store: the
/// application implements this from its own source, such as the permission claims its authentication scheme issues,
/// and registers it. It is asked only for an Operation that is not a continuation.
/// </summary>
public interface IActorPermissions
{
    /// <summary>Whether the Actor <paramref name="context"/> runs for, in its tenant, holds <paramref name="permission"/>.</summary>
    Task<bool> HasPermissionAsync(OperationContext context, string permission, CancellationToken cancellationToken);
}

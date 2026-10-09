using PouryaKit.Domain;

namespace PouryaKit.Security;

/// <summary>The Errors the permission check stops an Operation with.</summary>
public static class PermissionErrors
{
    /// <summary>The Actor does not hold the permission the command or query declares. Nothing ran.</summary>
    public static readonly Error Denied = Error.Forbidden(
        "Permission.Denied",
        "This operation requires a permission its actor does not hold."
    );
}

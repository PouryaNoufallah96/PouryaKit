using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The Errors the tenant requirement of a tenant-scoped Module or application, and the Unit of Work's save of a
/// tenant-scoped aggregate, stop an Operation with.
/// </summary>
public static class TenantErrors
{
    /// <summary>The Operation has no tenant, and every Operation of its Module needs one. Nothing ran.</summary>
    public static readonly Error Required = Error.Forbidden(
        "Tenant.Required",
        "This operation runs only for a tenant, and its context names none."
    );

    /// <summary>
    /// The Operation would save a tenant-scoped aggregate that is not its tenant's: one it added for another tenant, or
    /// one it changed or removed that belonged to another tenant when it was loaded, or whose tenant it changed. Nothing
    /// was saved.
    /// </summary>
    public static readonly Error Mismatch = Error.Forbidden(
        "Tenant.Mismatch",
        "This operation changes data of another tenant than its own, or moves data between tenants, so nothing was saved."
    );
}

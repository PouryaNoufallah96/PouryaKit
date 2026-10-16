using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>The Errors the tenant requirement of a tenant-scoped Module or application stops an Operation with.</summary>
public static class TenantErrors
{
    /// <summary>The Operation has no tenant, and every Operation of its Module needs one. Nothing ran.</summary>
    public static readonly Error Required = Error.Forbidden(
        "Tenant.Required",
        "This operation runs only for a tenant, and its context names none."
    );
}

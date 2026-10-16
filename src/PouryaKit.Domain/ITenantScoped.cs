namespace PouryaKit.Domain;

/// <summary>
/// Opt-in for an aggregate that belongs to one tenant. The aggregate sets <see cref="Tenant"/> itself, from the tenant
/// its handler takes from the Operation Context; the Kit never sets it. EF Core queries return only the rows of the
/// Operation's tenant; a plain SQL read says <c>tenant = @Tenant</c> itself. Implement it on an aggregate or an entity
/// that is not mapped as an owned type, at the root of its inheritance hierarchy or below a root that implements it too:
/// EF Core filters only those, so the model refuses to build otherwise.
/// </summary>
public interface ITenantScoped
{
    /// <summary>The tenant the aggregate belongs to; never blank.</summary>
    string Tenant { get; }
}

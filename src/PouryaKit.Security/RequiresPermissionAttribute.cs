namespace PouryaKit.Security;

/// <summary>
/// Declares the permission an Actor must hold to run this command or query. It is checked in every Module whose
/// Composition Root calls <c>AddPouryaKitPermissions</c> with its assembly; a trusted continuation of an authorized
/// Operation runs without the check (G.6).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresPermissionAttribute : Attribute
{
    public RequiresPermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    /// <summary>The permission's name, as the application's <see cref="IActorPermissions"/> knows it.</summary>
    public string Permission { get; }
}

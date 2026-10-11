using PouryaKit.Cqrs;

namespace PouryaKit.Security;

/// <summary>
/// Declares the permission an Actor must hold to run this command or query. It is checked in every Module whose
/// Composition Root calls <c>AddPouryaKitPermissions</c> for its assembly; a trusted continuation of an authorized
/// Operation runs without the check (G.6). A Host composed with Cqrs's <c>AddPouryaKitCompositionChecks</c>, which
/// <c>AddPouryaKitModules</c> calls and a Host without Modules calls itself, fails when its container is built if a
/// command or query it handles declares one and no Composition Root registered its check.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresPermissionAttribute : Attribute, IRequiresAuthorizer
{
    /// <summary>Declares that an Actor must hold <paramref name="permission"/> to run this command or query.</summary>
    public RequiresPermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    /// <summary>The permission's name, as the application's <see cref="IActorPermissions"/> knows it.</summary>
    public string Permission { get; }

    string IRequiresAuthorizer.Registration => nameof(SecurityServiceCollectionExtensions.AddPouryaKitPermissions);

    Type IRequiresAuthorizer.AuthorizerOf(Type operation) => typeof(PermissionAuthorizer<>).MakeGenericType(operation);
}

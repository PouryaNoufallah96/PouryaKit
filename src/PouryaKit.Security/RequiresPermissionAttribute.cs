using PouryaKit.Cqrs;

namespace PouryaKit.Security;

/// <summary>
/// Declares the permission an Actor must hold to run this command or query. It is checked in every Module whose
/// Composition Root calls <c>AddPouryaKitPermissions</c> with its assembly; a trusted continuation of an authorized
/// Operation runs without the check (G.6). A Host composed with <c>AddPouryaKitModules</c> fails when it is built if a
/// command or query it handles declares one and no Composition Root registered its check.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresPermissionAttribute : Attribute, IRequiresAuthorizer
{
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

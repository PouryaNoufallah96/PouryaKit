namespace PouryaKit.Cqrs;

/// <summary>
/// Implemented by a Kit attribute that, on a command or query, requires an authorizer of the Kit to be registered for
/// it, as Security's <c>RequiresPermissionAttribute</c> does; applications do not implement it. A Host composed with
/// <c>AddPouryaKitModules</c> fails when it is built if a command or query whose handler it registers carries one and
/// that authorizer is not registered, so a declared check never silently stays off.
/// </summary>
/// <remarks>
/// It is a public Cqrs type because nothing else reaches every Host. The failure it catches is a Module whose
/// Composition Root never called the attribute's registration, so that registration cannot carry the check, and the
/// Host's permission source is a plain registration that runs no Kit code. Modularity is the only Kit code that runs
/// after every Composition Root, and it may reference Cqrs only, not the attribute's package; the Kit has no
/// <c>InternalsVisibleTo</c> between packages.
/// </remarks>
public interface IRequiresAuthorizer
{
    /// <summary>The registration that adds the authorizer, named in the failure.</summary>
    string Registration { get; }

    /// <summary>The <see cref="IAuthorizer{TOperation}"/> implementation that must be registered for <paramref name="operation"/>.</summary>
    Type AuthorizerOf(Type operation);
}

namespace PouryaKit.Cqrs;

/// <summary>
/// Implemented by a Kit attribute that, on a command or query, requires an authorizer of the Kit to be registered for
/// it, as Security's <c>RequiresPermissionAttribute</c> does; applications do not implement it. A Host fails when it
/// starts if a command or query whose handler it registers carries one and that authorizer is not registered, so a
/// declared check never silently stays off: it is one of the composition checks of
/// <c>AddPouryaKitCompositionChecks</c>, which <c>AddPouryaKitModules</c> calls and a Host without Modules calls itself.
/// </summary>
/// <remarks>
/// It is a public Cqrs type because nothing else reaches every Host. The failure it catches is a Composition Root that
/// never called the attribute's registration, so that registration cannot carry the check, and the Host's permission
/// source is a plain registration that runs no Kit code. The check reads the Host's final registrations, which only
/// the container build sees, and the composition checks run from Cqrs, which may not reference the attribute's package;
/// the Kit's one <c>InternalsVisibleTo</c> between packages opens Cqrs to delivery and Web for restoring a trusted
/// continuation only.
/// </remarks>
public interface IRequiresAuthorizer
{
    /// <summary>The registration that adds the authorizer, named in the failure.</summary>
    string Registration { get; }

    /// <summary>The <see cref="IAuthorizer{TOperation}"/> implementation that must be registered for <paramref name="operation"/>.</summary>
    Type AuthorizerOf(Type operation);
}

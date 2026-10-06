namespace PouryaKit.Cqrs;

/// <summary>
/// Implemented by a Kit attribute that, on a command or query, requires an authorizer of the Kit to be registered for
/// it, as Security's <c>RequiresPermissionAttribute</c> does; applications do not implement it. A Host composed with
/// <c>AddPouryaKitModules</c> fails when it is built if a command or query whose handler it registers carries one and
/// that authorizer is not registered, so a declared check never silently stays off.
/// </summary>
public interface IRequiresAuthorizer
{
    /// <summary>The registration that adds the authorizer, named in the failure.</summary>
    string Registration { get; }

    /// <summary>The <see cref="IAuthorizer{TOperation}"/> implementation that must be registered for <paramref name="operation"/>.</summary>
    Type AuthorizerOf(Type operation);
}

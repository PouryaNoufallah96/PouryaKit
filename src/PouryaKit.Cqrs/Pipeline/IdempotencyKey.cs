namespace PouryaKit.Cqrs;

/// <summary>
/// The key of one idempotent request: the tenant, null where the Operation has none; the Actor; the command, as its
/// type's full name and its assembly's simple name (<c>Namespace.Type, Assembly</c>); and the request id.
/// </summary>
public sealed record IdempotencyKey(string? Tenant, Actor Actor, string Command, string RequestId);

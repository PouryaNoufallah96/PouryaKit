using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

// The Operation Context on an HTTP request between Kit Hosts, in the same names and format as PouryaKit.Messaging's
// OperationContextHeaders: one vocabulary for every transport. Web does not reference Messaging (plan section C), so it
// states the names again; the tests write and read them by Messaging's constants.
internal static class OperationContextHttpHeaders
{
    private const string ActorId = "pouryakit-actor-id";
    private const string ActorKindName = "pouryakit-actor-kind";
    private const string Tenant = "pouryakit-tenant";
    private const string CorrelationId = "pouryakit-correlation-id";
    private const string CausationId = "pouryakit-causation-id";

    private static readonly string[] Names = [ActorId, ActorKindName, Tenant, CorrelationId, CausationId];

    // Replaces rather than appends, so a request sent again carries the same single value of each.
    public static void Write(HttpRequestHeaders headers, OperationContext context)
    {
        foreach (var name in Names)
        {
            headers.Remove(name);
        }

        headers.TryAddWithoutValidation(ActorId, context.Actor.Id);
        headers.TryAddWithoutValidation(ActorKindName, context.Actor.Kind.ToString());
        if (context.Tenant is not null)
        {
            headers.TryAddWithoutValidation(Tenant, context.Tenant);
        }

        headers.TryAddWithoutValidation(CorrelationId, context.CorrelationId.ToString());
        headers.TryAddWithoutValidation(CausationId, context.OperationId.ToString());
    }

    // The continuation a trusted peer's request asks for: the same Actor, tenant and correlation, caused by the peer's
    // Operation, with an OperationId of its own. Null when the request carries none of the headers; a request that
    // carries some but not a complete, readable context is refused with 400, as a request that cannot be read is.
    public static OperationContext? Continued(IHeaderDictionary headers)
    {
        if (!Names.Any(headers.ContainsKey))
        {
            return null;
        }

        string Single(string name) =>
            headers.TryGetValue(name, out var values) && values is [{ Length: > 0 } value]
                ? value
                : throw new BadHttpRequestException($"The request continues an Operation but has no single {name} header.");

        var actorId = Single(ActorId);
        var actor = Single(ActorKindName) switch
        {
            nameof(ActorKind.Person) => Actor.Person(actorId),
            nameof(ActorKind.System) => Actor.System(actorId),
            var kind => throw new BadHttpRequestException($"The request's {ActorKindName} header {kind} is not an ActorKind."),
        };
        string? tenant = headers.ContainsKey(Tenant) ? Single(Tenant) : null;
        return new OperationContext(actor, tenant, Id(Single(CorrelationId), CorrelationId), Id(Single(CausationId), CausationId));
    }

    private static Guid Id(string value, string name) =>
        Guid.TryParse(value, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new BadHttpRequestException($"The request's {name} header is not a Guid.");
}

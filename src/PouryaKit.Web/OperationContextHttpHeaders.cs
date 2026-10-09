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
    private const int MaxValueLength = 256;

    private const string ActorId = "pouryakit-actor-id";
    private const string ActorKindName = "pouryakit-actor-kind";
    private const string Tenant = "pouryakit-tenant";
    private const string CorrelationId = "pouryakit-correlation-id";
    private const string CausationId = "pouryakit-causation-id";

    private static readonly string[] Names = [ActorId, ActorKindName, Tenant, CorrelationId, CausationId];

    internal static readonly string Rule = $"1 to {MaxValueLength} visible ASCII characters, with spaces only between them";

    // Every value is checked before any header is written, so a request with a value the rule refuses is never sent,
    // not even in part. Values replace rather than append, so a request sent again carries the same single value of each.
    public static void Write(HttpRequestHeaders headers, OperationContext context)
    {
        List<(string Name, string Value)> values = [(ActorId, context.Actor.Id), (ActorKindName, context.Actor.Kind.ToString())];
        if (context.Tenant is not null)
        {
            values.Add((Tenant, context.Tenant));
        }

        values.Add((CorrelationId, context.CorrelationId.ToString()));
        values.Add((CausationId, context.OperationId.ToString()));
        if (values.FirstOrDefault(header => !IsHeaderValue(header.Value)).Name is { } refused)
        {
            throw new InvalidOperationException(
                $"The Operation Context's {refused} header value is not {Rule}, so the request is not sent."
            );
        }

        foreach (var name in Names)
        {
            headers.Remove(name);
        }

        foreach (var (name, value) in values)
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }

    // The continuation a trusted peer's request asks for: the same Actor, tenant and correlation, caused by the peer's
    // Operation, with an OperationId of its own. Called only for a caller authenticated as a trusted peer, so the context
    // is restored as a trusted continuation (ADR 0007): the permission check does not authorize its Actor again. Null
    // when the request carries none of the headers; a request that carries some but not a complete, readable context is
    // refused with 400, as a request that cannot be read is.
    public static OperationContext? Continued(IHeaderDictionary headers)
    {
        if (!Names.Any(headers.ContainsKey))
        {
            return null;
        }

        string Single(string name) =>
            headers.TryGetValue(name, out var values) && values is [{ } value] && IsHeaderValue(value)
                ? value
                : throw new KitBadRequestException($"The request continues an Operation but has no single {name} header that is {Rule}.");

        var actorId = Single(ActorId);
        var actor = Single(ActorKindName) switch
        {
            nameof(ActorKind.Person) => Actor.Person(actorId),
            nameof(ActorKind.System) => Actor.System(actorId),
            var kind => throw new KitBadRequestException($"The request's {ActorKindName} header {kind} is not an ActorKind."),
        };
        string? tenant = headers.ContainsKey(Tenant) ? Single(Tenant) : null;
        return OperationContext.RestoreTrusted(
            actor,
            tenant,
            Id(Single(CorrelationId), CorrelationId),
            Id(Single(CausationId), CausationId)
        );
    }

    // The Kit's rule for a context header value, on both sides of a call, and for an Idempotency-Key: no control
    // character can end a header line or start another, nothing outside ASCII depends on an encoding, and no edge space
    // is trimmed away in transit.
    internal static bool IsHeaderValue(string value) =>
        value.Length is > 0 and <= MaxValueLength
        && value[0] != ' '
        && value[^1] != ' '
        && !value.AsSpan().ContainsAnyExceptInRange(' ', '~');

    private static Guid Id(string value, string name) =>
        Guid.TryParse(value, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new KitBadRequestException($"The request's {name} header is not a Guid.");
}

using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

/// <summary>
/// The Operation Context on a request between Kit Hosts, in the same names and format as PouryaKit.Messaging's
/// <c>OperationContextHeaders</c>: one vocabulary, and one codec for every transport that carries it in headers.
/// </summary>
public static class OperationContextHttpHeaders
{
    private const int MaxValueLength = 256;

    // Web does not reference Messaging (plan section C), so it states the names again; the tests write and read them by
    // Messaging's constants.
    private const string ActorId = "pouryakit-actor-id";
    private const string ActorKindName = "pouryakit-actor-kind";
    private const string Tenant = "pouryakit-tenant";
    private const string CorrelationId = "pouryakit-correlation-id";
    private const string CausationId = "pouryakit-causation-id";

    private static readonly string[] Names = [ActorId, ActorKindName, Tenant, CorrelationId, CausationId];

    internal static readonly string Rule = $"1 to {MaxValueLength} visible ASCII characters, with spaces only between them";

    /// <summary>
    /// The header pairs that carry <paramref name="context"/> to a Kit Host that trusts the caller as a peer: the
    /// Actor's id and kind, the tenant when there is one, the correlation, and the context's <c>OperationId</c> as the
    /// causation. Every value is checked before any pair is returned, so a request is never sent with a value the
    /// receiving Host refuses, not even in part.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not 1 to 256 visible ASCII characters with spaces only
    /// between them.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> Pairs(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<KeyValuePair<string, string>> pairs = [new(ActorId, context.Actor.Id), new(ActorKindName, context.Actor.Kind.ToString())];
        if (context.Tenant is not null)
        {
            pairs.Add(new(Tenant, context.Tenant));
        }

        pairs.Add(new(CorrelationId, context.CorrelationId.ToString()));
        pairs.Add(new(CausationId, context.OperationId.ToString()));
        if (pairs.FirstOrDefault(pair => !IsHeaderValue(pair.Value)).Key is { } refused)
        {
            throw new InvalidOperationException(
                $"The Operation Context's {refused} header value is not {Rule}, so the request is not sent."
            );
        }

        return pairs;
    }

    // Values replace rather than append, so a request sent again carries the same single value of each.
    internal static void Write(HttpRequestHeaders headers, OperationContext context)
    {
        var pairs = Pairs(context);
        foreach (var name in Names)
        {
            headers.Remove(name);
        }

        foreach (var (name, value) in pairs)
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }

    // The continuation a trusted peer's request asks for: the same Actor, tenant and correlation, caused by the peer's
    // Operation, with an OperationId of its own. Called only for a caller authenticated as a trusted peer, so the context
    // is restored as a trusted continuation (ADR 0007): the permission check does not authorize its Actor again. Null
    // when the request carries none of the headers; a request that carries some but not a complete, readable context is
    // refused with 400, as a request that cannot be read is.
    internal static OperationContext? Continued(IHeaderDictionary headers)
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
            _ => throw new KitBadRequestException(
                $"The request's {ActorKindName} header is {nameof(ActorKind.Person)} or {nameof(ActorKind.System)}."
            ),
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

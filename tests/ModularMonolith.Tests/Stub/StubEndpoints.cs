using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// Test-only endpoints of the Stub Module, mapped under /stub. Catalog has no Feature that fails with a conflict or
// throws, so these reach those outcomes through the Stub Module's Gateway.

internal sealed class ReadOperationContextEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/context", ReadAsync);
        routes.MapGet("/public/context", ReadAsync).AllowAnonymousAs(Visitor);
        routes.MapGet("/kiosk/context", ReadAsync).AllowAnonymousAs(Kiosk);
    }

    public static Actor Visitor { get; } = Actor.System("stub.visitor");

    /// <summary>The Actor of a second public endpoint, whose anonymous callers are not <see cref="Visitor"/>'s.</summary>
    public static Actor Kiosk { get; } = Actor.System("stub.kiosk");

    private static async Task<Microsoft.AspNetCore.Http.IResult> ReadAsync(
        OperationContext context,
        IGatewayProcessor<StubModule> stub,
        CancellationToken cancellationToken
    ) => (await stub.QueryAsync(new ReadOperationContextQuery(), context, cancellationToken)).ToHttpResult();
}

internal sealed class RejectedNoteEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/notes/rejected",
            async (OperationContext context, IGatewayProcessor<StubModule> stub, CancellationToken cancellationToken) =>
                (
                    await stub.SendAsync(new WriteNoteThenFailCommand("rejected", Ending.Fail), context, cancellationToken)
                ).ToHttpResult()
        );
}

internal sealed class ThrowingNoteEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/notes/throwing",
            async (OperationContext context, IGatewayProcessor<StubModule> stub, CancellationToken cancellationToken) =>
                (
                    await stub.SendAsync(new WriteNoteThenFailCommand("throwing", Ending.Throw), context, cancellationToken)
                ).ToHttpResult()
        );
}

// An [Idempotent] command on PUT and on DELETE, the methods no idempotent command of Catalog is mapped on.
internal sealed class CountedNoteEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPut("/counted-notes/{text}", CountAsync).RequireIdempotencyKey();
        routes.MapDelete("/counted-notes/{text}", CountAsync).RequireIdempotencyKey();
    }

    private static async Task<Microsoft.AspNetCore.Http.IResult> CountAsync(
        string text,
        OperationContext context,
        IGatewayProcessor<StubModule> stub,
        CancellationToken cancellationToken
    ) => (await stub.SendAsync(new CountNoteCommand(text), context, cancellationToken)).ToHttpResult();
}

/// <summary>Registered only by a test that builds a Host of its own, to map <see cref="PlainAnonymousEndpoint"/>.</summary>
internal sealed class PlainAnonymousRoute;

// A public endpoint that names no Actor for its anonymous callers, which the Host must refuse. It is mapped only in a
// Host that registers PlainAnonymousRoute, so the shared Host still starts.
internal sealed class PlainAnonymousEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<PlainAnonymousRoute>() is not null)
        {
            routes.MapGet("/plain-anonymous", () => "anyone").AllowAnonymous();
        }
    }
}

/// <summary>Registered only by a test that builds a Host of its own, to map <see cref="KeyedReadEndpoint"/>.</summary>
internal sealed class KeyedReadRoute;

// A read that requires an Idempotency-Key, which the Host must refuse: its query ignores the key. It is mapped only in a
// Host that registers KeyedReadRoute, so the shared Host still starts.
internal sealed class KeyedReadEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<KeyedReadRoute>() is not null)
        {
            routes.MapGet("/keyed-read", () => "read").RequireIdempotencyKey();
        }
    }
}

/// <summary>Maps <see cref="KeyedAnyMethodEndpoint"/>, without method metadata or with an empty method list.</summary>
internal sealed record KeyedAnyMethodRoute(bool EmptyMethodList);

// An endpoint that requires an Idempotency-Key but names no HTTP method, which the Host must refuse: it answers every
// method, GET included, whose query ignores the key. It is mapped only in a Host that registers KeyedAnyMethodRoute.
internal sealed class KeyedAnyMethodEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<KeyedAnyMethodRoute>() is { } route)
        {
            var endpoint = routes.Map("/keyed-any", () => "any").RequireIdempotencyKey();
            if (route.EmptyMethodList)
            {
                endpoint.WithMetadata(new HttpMethodMetadata([]));
            }
        }
    }
}

internal sealed class MixedErrorsEndpoint : IEndpoint
{
    public static readonly ImmutableArray<Error> Errors =
    [
        Error.Validation("Note.TextRequired", "A note needs text.", "Text"),
        Error.NotFound("Note.Missing", "The note does not exist."),
        Error.Conflict("Note.Rejected", "The note was rejected."),
    ];

    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/mixed-errors", () => ((Result)Errors).ToHttpResult());
}

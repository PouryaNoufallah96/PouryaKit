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
    }

    public static Actor Visitor { get; } = Actor.System("stub.visitor");

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

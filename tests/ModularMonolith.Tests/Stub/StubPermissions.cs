using System.Collections.Concurrent;
using Catalog;
using Catalog.Products.Features.RemoveProducts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Security;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// A command that declares a permission, sent by a Notification Handler as the continuation of the Operation that raised
// the Domain Event, with an application authorizer of its own beside the permission check.

[RequiresPermission(StampNoteCommand.Permission)]
internal sealed record StampNoteCommand(string Text) : Command
{
    public const string Permission = "stub.notes.stamp";

    /// <summary>A text the application authorizer refuses to stamp.</summary>
    public const string RefusedPrefix = "refused ";

    public static string Stamped(string text) => $"stamped {text}";
}

internal sealed class StampNoteCommandHandler(IRepository<Note, NoteId> notes) : CommandHandler<StampNoteCommand>
{
    public override async Task<Result> Handle(StampNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(StampNoteCommand.Stamped(command.Text)), cancellationToken);
        return Result.Success();
    }
}

/// <summary>The Operation Contexts the application authorizer of <see cref="StampNoteCommand"/> ran with, by text.</summary>
internal sealed class StampAuthorizations
{
    private readonly ConcurrentQueue<(string Text, OperationContext Context)> _seen = [];

    public IReadOnlyList<OperationContext> For(string text) => [.. _seen.Where(seen => seen.Text == text).Select(seen => seen.Context)];

    public void Add(string text, OperationContext context) => _seen.Enqueue((text, context));
}

// Found by the Stub Module's assembly scan, as an application's own authorizer is.
internal sealed class RefuseSomeStampsAuthorizer(OperationContext context, StampAuthorizations seen)
    : IAuthorizer<StampNoteCommand>
{
    public static readonly Error Refused = Error.Forbidden("Note.StampRefused", "This note may not be stamped.");

    public Task<Result> AuthorizeAsync(StampNoteCommand operation, CancellationToken cancellationToken)
    {
        seen.Add(operation.Text, context);
        return Task.FromResult(operation.Text.StartsWith(StampNoteCommand.RefusedPrefix, StringComparison.Ordinal) ? Refused : Result.Success());
    }
}

// A query that declares a permission: a read is protected the same way as a command.
[RequiresPermission(ReadStampLedgerQuery.Permission)]
internal sealed record ReadStampLedgerQuery : Query<string>
{
    public const string Permission = "stub.stamps.read";

    public const string Ledger = "the stamp ledger";
}

internal sealed class ReadStampLedgerQueryHandler : QueryHandler<ReadStampLedgerQuery, string>
{
    public override Task<Result<string>> Handle(ReadStampLedgerQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(ReadStampLedgerQuery.Ledger);
}

internal sealed record NoteToStampDomainEvent(string Text) : DomainEvent;

internal sealed class StampNoteNotificationHandler(IDispatcher dispatcher) : INotificationHandler<NoteToStampDomainEvent>
{
    public Task<Result> Handle(NoteToStampDomainEvent domainEvent, CancellationToken cancellationToken) =>
        dispatcher.SendAsync(new StampNoteCommand(domainEvent.Text), cancellationToken);
}

// Catalog's permission-protected command over HTTP: Catalog maps no endpoint for it.
internal sealed class RemoveCatalogProductsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/catalog-products/removal",
            async (
                RemoveCatalogProductsBody body,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) => (await catalog.SendAsync(new RemoveProductsCommand(body.Names), context, cancellationToken)).ToHttpResult()
        );
}

internal sealed record RemoveCatalogProductsBody(IReadOnlyList<string> Names);

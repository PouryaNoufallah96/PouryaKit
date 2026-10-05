using Catalog.Contracts.Products;
using Catalog.Products.Features.GetProduct;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record ReadOperationContextQuery : Query<OperationContext>;

internal sealed class ReadOperationContextQueryHandler(OperationContext context)
    : QueryHandler<ReadOperationContextQuery, OperationContext>
{
    public override Task<Result<OperationContext>> Handle(
        ReadOperationContextQuery query,
        CancellationToken cancellationToken
    ) => Task.FromResult<Result<OperationContext>>(context);
}

// The asking side of a synchronous question between Modules: Stub asks Catalog through Catalog's Contracts only.
internal sealed record NoteProductCommand(Guid ProductId) : Command;

internal sealed class NoteProductCommandHandler(ICatalogProducts catalog, IRepository<Note, NoteId> notes)
    : CommandHandler<NoteProductCommand>
{
    public static readonly Error UnknownProduct = Error.NotFound("Note.UnknownProduct", "Catalog has no such product.");

    public override async Task<Result> Handle(NoteProductCommand command, CancellationToken cancellationToken)
    {
        var product = await catalog.FindProductAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return UnknownProduct;
        }

        await notes.AddAsync(Note.Write(product.Name), cancellationToken);
        return Result.Success();
    }
}

/// <summary>The Operation Contexts Catalog's product question ran with, in order.</summary>
internal sealed class CatalogQuestionContexts
{
    public List<OperationContext> Seen { get; } = [];
}

// Registered by StubModule's assembly scan, so the test sees the context Catalog's Operation reads without changing
// Catalog. Reading the Operation Context is what an authorizer is for.
internal sealed class RecordCatalogQuestionContext(OperationContext context, CatalogQuestionContexts contexts)
    : IAuthorizer<GetProductQuery>
{
    public Task<Result> AuthorizeAsync(GetProductQuery operation, CancellationToken cancellationToken)
    {
        contexts.Seen.Add(context);
        return Task.FromResult(Result.Success());
    }
}

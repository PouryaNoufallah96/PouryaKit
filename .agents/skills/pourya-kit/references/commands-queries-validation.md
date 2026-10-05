# Commands, queries and validation

`PouryaKit.Cqrs`, `PouryaKit.Validation`: commands and queries, their handlers, the fixed pipeline and validators. Namespaces `PouryaKit.Cqrs` and `PouryaKit.Validation`; validators are FluentValidation's `AbstractValidator<T>`.

A Feature with a command, its validator and its handler, and a query with its handler. Every type is `internal`:

```csharp
using FluentValidation;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

// Products/Features/CreateProduct/
internal sealed record CreateProductCommand(Guid ProductId, Guid CategoryId, string Name) : Command<Guid>;

internal sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();      // checked before the typed-ID constructor can throw
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithErrorCode("Product.NameTooLong");
    }
}

internal sealed class CreateProductCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<CreateProductCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(CreateProductCommand command, CancellationToken ct)
    {
        var product = Product.Create(new ProductId(command.ProductId), new CategoryId(command.CategoryId), command.Name);
        if (product.IsFailure) return product.Errors;

        await products.AddAsync(product.Value, ct);
        return product.Value.Id.Value;             // no SaveChanges, no transaction, no Outbox
    }
}

// Products/Features/GetProduct/
internal sealed record GetProductQuery(Guid ProductId) : Query<ProductDto>;

internal sealed class GetProductQueryHandler(IProductReader reader) : QueryHandler<GetProductQuery, ProductDto>
{
    public override async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken ct) =>
        await reader.FindAsync(query.ProductId, ct) is { } product ? product : ProductErrors.NotFound;
}
```

`IRepository<,>` is the Kit's generic repository (`PouryaKit.Persistence`, see [persistence.md](persistence.md)). `IProductReader` stands for the Feature's read access; Dapper reads arrive with #9.

## Commands and queries

- Declare each as a sealed record: `Command<TResponse>` for a command with a value, `Command` for one without (its handler derives from `CommandHandler<TCommand>` and returns `Result`), `Query<TResponse>` for a read. Name it after its intent with the `Command` or `Query` suffix.
- Give each exactly one handler class deriving from `CommandHandler<,>`, `CommandHandler<>` or `QueryHandler<,>`. Keep both `internal`; the Kit finds them without `InternalsVisibleTo`. A command or query with no handler or several fails at startup.
- Commands, queries, handlers and authorizers are never generic type definitions (no `EchoQuery<T>`): registration rejects them at startup.
- A handler returns a Result and never saves, commits, begins a transaction or touches the Outbox: the Unit of Work does all of that once, for the whole Operation.
- A query reads. Its handler never sends a command: that throws, because a query runs without a Unit of Work.
- A Domain Event Handler (`IDomainEventHandler<TDomainEvent>`, also in `PouryaKit.Cqrs`) is the third kind of handler. The Unit of Work runs it, so it is taught in [persistence.md](persistence.md).

## The pipeline

- Every command and query passes the same fixed steps: logging, the authorization hook, validation, then the handler; a command's handler runs inside the Unit of Work. Never register a pipeline step or a behaviour yourself.
- Send from outside the Module through its Gateway (see [modules.md](modules.md)). Inside a handler, send a nested command with an injected `IDispatcher`: `await dispatcher.SendAsync(new ReserveStockCommand(id), ct)`. It joins the outer Unit of Work, so the Operation commits once.
- A nested command that fails, in its handler, its validation or its authorization, or by throwing, rolls the whole Operation back, even if your handler ignores its Result. The Operation then fails with that nested command's Errors. Pass a failure on with `return result.Errors;` instead of ignoring it.
- A failed nested query rolls nothing back; act on its Result as you need.
- One scope holds one Operation. Never send a second top-level command in a scope that already ran one, and never retry a failed command in the same scope: send it again through the Gateway, which opens a new scope for each call.

## Validation

- Write one `AbstractValidator<T>` per command or query that needs input checks, beside it in the Feature, and keep it `internal`. Several validators for one type all run.
- Every failure of every validator comes back as one failed Result before the handler runs; nothing throws. Each failure is an Error of kind `Validation`, with FluentValidation's error code as `Code` (set your own with `WithErrorCode`) and the property name as `Target`.
- Validate input shape here. A business rule that depends on state belongs in the aggregate, through `CheckRule`.
- Without `PouryaKit.Validation`, the validation step does nothing.

## Authorization hook

- The Actor is authenticated and authorized at the external entry point. Add a per-operation rule only when one command or query needs it: an `internal` class implementing `IAuthorizer<TOperation>` for that one concrete command or query, returning `Result.Success()` or a `Forbidden` Error. It runs before validation. Without one, the step does nothing. An authorizer for a base type, a marker interface or an open type fails at startup, because it would never run.
- An authorizer or a validator may run a query (a permission or existence lookup), but never sends a command: it runs before its command's handler, at every nesting depth, so that throws.

## Registration

```csharp
module.AddPouryaKitCqrs(typeof(CatalogModule).Assembly);        // handlers, authorizers, Domain Event Handlers
module.AddPouryaKitValidation(typeof(CatalogModule).Assembly);  // validators
```

Make both calls on the Module's builder in its Composition Root (see [modules.md](modules.md)). The Unit of Work comes from the Module's persistence package, and logging from the Host.

## Testing

- Test a Feature by sending it through its Module's Gateway against the hosted application (see [testing.md](testing.md)). Assert on the returned Result's `Errors`, never on exceptions, for business and validation failures.

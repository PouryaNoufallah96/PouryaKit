# Domain primitives

`PouryaKit.Domain`: Result and Error, typed IDs, entities, value objects, aggregates, Domain Events and business rules. One namespace, `PouryaKit.Domain`; no dependencies.

- [Result and Error](#result-and-error)
- [Typed IDs](#typed-ids)
- [Aggregates, entities and value objects](#aggregates-entities-and-value-objects)
- [Domain Events](#domain-events)
- [Business rules](#business-rules)
- [The clock](#the-clock)
- [Testing](#testing)

An aggregate with a typed ID that checks a business rule, raises a Domain Event and returns a Result:

```csharp
using PouryaKit.Domain;

internal sealed record ProductId(Guid Value) : TypedId<Guid>(Value);

internal sealed record CategoryId(Guid Value) : TypedId<Guid>(Value);

internal sealed record ProductCreatedDomainEvent(ProductId ProductId, CategoryId CategoryId, string Name) : DomainEvent;

internal static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation("Product.NameRequired", "A product needs a name.");
    public static readonly Error NotFound = Error.NotFound("Product.NotFound", "The product does not exist.");
}

internal sealed class ProductNameMustNotBeBlank(string name) : IBusinessRule
{
    public Error Error => ProductErrors.NameRequired;

    public bool IsBroken() => string.IsNullOrWhiteSpace(name);
}

internal sealed class Product : AggregateRoot<ProductId>
{
    public string Name { get; private set; } = "";

    public CategoryId CategoryId { get; private set; } = null!;

    public static Result<Product> Create(ProductId id, CategoryId categoryId, string name)
    {
        var rule = CheckRule(new ProductNameMustNotBeBlank(name));
        if (rule.IsFailure) return rule.Errors;

        var product = new Product { Id = id, CategoryId = categoryId, Name = name.Trim() };
        product.Raise(new ProductCreatedDomainEvent(id, categoryId, product.Name));
        return product;
    }
}

// Elsewhere: var created = Product.Create(TypedId.New<ProductId>(), new CategoryId(command.CategoryId), command.Name);
```

## Result and Error

- Return a value or an Error directly; each converts to a Result. `Result.Success()` is the success without a value. When the value's static type is an interface (such as `IReadOnlyList<T>`), C# applies no implicit conversion, so return `Result.Success(value)`.
- Pass a failure on with `return result.Errors;`; it converts to a failed Result of any type and keeps every Error.
- Check `IsSuccess` or `IsFailure` before reading `Value`; reading it from a failure throws. No conversion turns a Result into its value, and a null value throws instead of becoming a success.
- Give each Error a stable code (`Product.NameRequired`), a message and a kind through its factory: `Validation`, `NotFound`, `Conflict`, `Forbidden` or `Unexpected`. Never an HTTP status; the Web package maps kinds to responses.
- Declare a Business Area's Errors once, as static fields.

## Typed IDs

- Declare one per aggregate as a sealed record over `Guid`, `long`, `int` or `string`, as above. Two IDs are equal only when type and value are equal, so IDs of different aggregates cannot be mixed up.
- Create a new Guid ID with `TypedId.New<ProductId>()`; it is time-ordered (version 7). Wrap a value from input with `new ProductId(value)`.
- The constructor throws on an empty Guid or a blank string: that is a bug, so validate input before it reaches the constructor.
- Write no converter. JSON needs `TypedIdJsonConverterFactory` once in the serializer options. Until the Web (#14) and Messaging packages register it, add it to your serializer options yourself. It also handles typed IDs as dictionary keys, and reports an empty ID in JSON as a `JsonException`. EF Core needs nothing either: `PouryaKitDbContext` stores every typed ID as its value (see [persistence.md](persistence.md)). Dapper registration arrives with #9.

## Aggregates, entities and value objects

- Raise a Domain Event with `Raise` from inside the aggregate. Never clear Domain Events and never set `Version`: the Unit of Work owns both. It takes and clears Domain Events when it runs their handlers (see [persistence.md](persistence.md)); versions arrive with #11. Raising an event does not change the version.
- Entities are equal when type and ID are equal; do not override equality.
- Declare a value object as a record deriving from `ValueObject`. Its equality covers every member; a collection member compares by reference, so wrap it in a value object of its own.

## Domain Events

- Declare each as a sealed record deriving from `DomainEvent`, named in the past tense with the `DomainEvent` suffix. Its `Id` and UTC `OccurredOn` are assigned once when it is created and survive serialization; never set them yourself.
- Carry typed IDs and values, never the aggregate itself or its version.
- Serialize a Domain Event by its runtime type (`JsonSerializer.Serialize(e, e.GetType(), options)`); serializing it as `DomainEvent` drops the derived members.
- A payload without `Id` deserializes with a new identity, so a stored or sent event must always include its `Id`.
- React to one inside the Module with a Domain Event Handler; see [persistence.md](persistence.md).

## Business rules

- Implement `IBusinessRule` with `IsBroken()` and the Error it returns. `CheckRule(rule)` in an entity, aggregate or value object returns a Result; a broken rule is a failed Result, never an exception.

## The clock

- A Domain Event takes its `OccurredOn` from the running Operation's clock: the dispatcher binds the Host's registered `TimeProvider` as `DomainClock` for each Operation. Outside an Operation, `DomainClock` is the system clock.
- To change the time Operations see, the Host registers its own `TimeProvider` (`builder.Services.AddSingleton<TimeProvider>(time)`); `AddPouryaKitCqrs` adds `TimeProvider.System` only when none is registered.
- A business rule that compares with "now" takes the time as a parameter. The handler injects `TimeProvider` and passes `timeProvider.GetUtcNow()` in. Feature code takes time only from that `TimeProvider` and never binds `DomainClock`.

```csharp
internal sealed class SaleMustBeOpen(DateTimeOffset endsOn, DateTimeOffset now) : IBusinessRule
{
    public Error Error => Error.Conflict("Sale.Ended", "The sale has ended.");

    public bool IsBroken() => now >= endsOn;
}

// In the aggregate: public Result Buy(DateTimeOffset now) => CheckRule(new SaleMustBeOpen(EndsOn, now));
// In its handler, with TimeProvider timeProvider injected: var bought = sale.Buy(timeProvider.GetUtcNow());
```

## Testing

- Test an aggregate through its public methods and assert on `DomainEvents` and on the returned Result's `Errors`.
- To fix the time of Domain Events an aggregate raises in a test, create a `TestClock` (`PouryaKit.Testing`) with `using` inside the test method itself: `using var clock = new TestClock(monday);`, then `clock.Time.Advance(...)`. A binding made in a constructor or fixture is not relied on to reach the test.

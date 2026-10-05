---
name: pourya-kit
description: How to build a .NET modular monolith on PouryaKit. Use when writing, reviewing or testing code in a project that references PouryaKit packages, such as a Module, an aggregate, a command or query, a Domain Event Handler, a Dapper read, an Integration Event Handler or an endpoint.
---

# PouryaKit

PouryaKit lets Feature code hold only business behaviour. The Feature supplies the aggregate, the command or query, its validator and handler, its data access and its endpoint. The Kit dispatches the Operation, validates it, owns its single commit, runs its reactions and delivers its Integration Events and Snapshots to other Modules.

These hold in every Capability:

- A handler returns a Result. An expected business failure is an Error in that Result; exceptions are for bugs and infrastructure.
- The Unit of Work saves, commits and writes the Outbox. Feature code returns its Result and leaves all three to it.
- Anything outside a Module reaches it through its Gateway.
- Names follow the Kit's `GLOSSARY.md`: Module, Feature, Operation, Domain Event, Notification Handler, Integration Event, Snapshot.

## Capabilities

Each section below is filled by the ticket that delivers its Capability. A section that reads *Not yet available* has no API yet: work from the ticket it names, not from the package name.

### Domain primitives

`PouryaKit.Domain`: Result and Error, typed IDs, entities, value objects, aggregates, Domain Events and business rules. One namespace, `PouryaKit.Domain`; no dependencies.

An aggregate with a typed ID that checks a business rule, raises a Domain Event and returns a Result:

```csharp
using PouryaKit.Domain;

internal sealed record ProductId(Guid Value) : TypedId<Guid>(Value);

internal sealed record ProductCreatedDomainEvent(ProductId ProductId, string Name) : DomainEvent;

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

    public static Result<Product> Create(ProductId id, string name)
    {
        var rule = CheckRule(new ProductNameMustNotBeBlank(name));
        if (rule.IsFailure) return rule.Errors;

        var product = new Product { Id = id, Name = name.Trim() };
        product.Raise(new ProductCreatedDomainEvent(id, product.Name));
        return product;
    }
}

// Elsewhere: var created = Product.Create(TypedId.New<ProductId>(), command.Name);
```

**Result and Error**

- Return a value or an Error directly; each converts to a Result. `Result.Success()` is the success without a value. When the value's static type is an interface (such as `IReadOnlyList<T>`), C# applies no implicit conversion, so return `Result.Success(value)`.
- Pass a failure on with `return result.Errors;`; it converts to a failed Result of any type and keeps every Error.
- Check `IsSuccess` or `IsFailure` before reading `Value`; reading it from a failure throws. No conversion turns a Result into its value, and a null value throws instead of becoming a success.
- Give each Error a stable code (`Product.NameRequired`), a message and a kind through its factory: `Validation`, `NotFound`, `Conflict`, `Forbidden` or `Unexpected`. Never an HTTP status; the Web package maps kinds to responses.
- Declare a Business Area's Errors once, as static fields.

**Typed IDs**

- Declare one per aggregate as a sealed record over `Guid`, `long`, `int` or `string`, as above. Two IDs are equal only when type and value are equal, so IDs of different aggregates cannot be mixed up.
- Create a new Guid ID with `TypedId.New<ProductId>()`; it is time-ordered (version 7). Wrap a value from input with `new ProductId(value)`.
- The constructor throws on an empty Guid or a blank string: that is a bug, so validate input before it reaches the constructor.
- Write no converter. JSON needs `TypedIdJsonConverterFactory` once in the serializer options. Until the Web (#14) and Messaging packages register it, add it to your serializer options yourself. It also handles typed IDs as dictionary keys, and reports an empty ID in JSON as a `JsonException`. EF Core and Dapper registration arrive with those packages.

**Aggregates, entities and value objects**

- Raise a Domain Event with `Raise` from inside the aggregate. Never clear Domain Events and never set `Version`: the Unit of Work does both, and raising an event does not change the version.
- Entities are equal when type and ID are equal; do not override equality.
- Declare a value object as a record deriving from `ValueObject`. Its equality covers every member; a collection member compares by reference, so wrap it in a value object of its own.

**Domain Events**

- Declare each as a sealed record deriving from `DomainEvent`, named in the past tense with the `DomainEvent` suffix. Its `Id` and UTC `OccurredOn` are assigned once when it is created and survive serialization; never set them yourself.
- Carry typed IDs and values, never the aggregate itself or its version.
- Serialize a Domain Event by its runtime type (`JsonSerializer.Serialize(e, e.GetType(), options)`); serializing it as `DomainEvent` drops the derived members.
- A payload without `Id` deserializes with a new identity, so a stored or sent event must always include its `Id`.

**Business rules**

- Implement `IBusinessRule` with `IsBroken()` and the Error it returns. `CheckRule(rule)` in an entity, aggregate or value object returns a Result; a broken rule is a failed Result, never an exception.

**Testing**

- Test an aggregate through its public methods and assert on `DomainEvents` and on the returned Result's `Errors`.

### Commands, queries and validation

`PouryaKit.Cqrs`, `PouryaKit.Validation`: commands and queries, their handlers, the fixed pipeline and validators.

*Not yet available* (#7).

### Modules

`PouryaKit.Modularity`: the Composition Root, the Gateway, Operation scopes and the Operation Context.

*Not yet available* (#8, #13).

### Persistence and the Unit of Work

`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`: repositories and specifications, a DbContext and schema per Module, Domain Event Handlers, versions and conflicts.

*Not yet available* (#8, #10, #11, #12).

### Dapper reads

`PouryaKit.Persistence.Dapper`: read queries in plain SQL on a pooled connection.

*Not yet available* (#9).

### Messaging

`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`: Notification Handlers, Integration Events and Integration Event Handlers, Snapshots and Tombstones.

*Not yet available* (#15, #16, #17, #18).

### Web

`PouryaKit.Web`: endpoints beside their Feature, Result to HTTP, the Operation Context from the caller.

*Not yet available* (#14).

### Testing

`PouryaKit.Testing`: PostgreSQL in a container, data reset between tests, Operations sent through a Gateway, delivery assertions.

*Not yet available* (#8, #15).

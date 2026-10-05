# PouryaKit.Domain provenance

Adopted implementations in `PouryaKit.Domain`. Format and rules: [README](README.md).

## Package dependencies

None. `PouryaKit.Domain` references no package and no other Kit package, as plan section C lists.

## Result and Error

- **Kit files:** `src/PouryaKit.Domain/Result.cs`, `src/PouryaKit.Domain/Error.cs`, `src/PouryaKit.Domain/ErrorKind.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/ResultPattern/Result.cs`, `src/BuildingBlocks/BuildingBlocks.Core/ErrorHandling/AppError.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #6
- **Intentional differences:**
  - An Error has a semantic `ErrorKind` (validation, not found, conflict, forbidden, unexpected) instead of an HTTP status as its code; `Code` is the Error's stable name. The title and the localised resource messages are dropped, so the domain knows no transport.
  - The `Result<T>` to `T` conversion is removed, and a null value no longer converts to a success (upstream `Result.cs:139`): it throws. `Result.Create`, which turned null into a placeholder Error, and `Result<T>.None` are removed.
  - Reading `Value` from a failure throws instead of returning the default.
  - `Errors` is an `ImmutableArray<Error>`, empty on success, never null. A failure needs at least one Error, and an `ImmutableArray<Error>` converts to a failed Result of any type, so a handler passes every Error of a failed Result on with `return result.Errors;`.
  - The constructor is `private protected`, so no type outside the package can derive a Result with other rules. The JSON attributes and the equality members are dropped.
  - A validation Error may name the input it is about in `Target` (ticket #7), through an overload of `Error.Validation`; every other Error has none. Upstream `AppError` has no such field; the Kit adds it so a transport can group validation Errors by input (RFC 9457 `errors`).

## Typed IDs

- **Kit files:** `src/PouryaKit.Domain/TypedId.cs`, `src/PouryaKit.Domain/TypedIdJsonConverterFactory.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Domain/Primitives/TypedIdValueBase.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #6
- **Intentional differences:**
  - Generic over Guid, long, int and string (G.3) instead of Guid only; any other value type is rejected.
  - `Value` is getter-only. An empty Guid or a blank string is rejected without Ardalis.GuardClauses, so the package has no dependency.
  - New Guid IDs come from `TypedId.New<TId>()` as version 7 Guids.
  - No implicit conversion to the raw value; `ToString` is sealed and culture-invariant.
  - `TypedId.From` creates any typed ID from its value through a constructor looked up once per type, so no ID declares a factory, and `TypedId.GetValueType` recognises typed IDs. The JSON converter factory is new and built on both.
  - The EF Core converter selector and the Dapper type handler (`src/BuildingBlocks/BuildingBlocks.Core/Persistence/Converters/StronglyTypedIdValueConverterSelector.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Converters/TypedIdValueHandler.cs`) are not adopted here; the EF Core and Dapper packages register typed IDs through `TypedId.GetValueType` and `TypedId.From`.

## Entity, aggregate root, value object and business rules

- **Kit files:** `src/PouryaKit.Domain/Entity.cs`, `src/PouryaKit.Domain/AggregateRoot.cs`, `src/PouryaKit.Domain/IAggregateRoot.cs`, `src/PouryaKit.Domain/ValueObject.cs`, `src/PouryaKit.Domain/IBusinessRule.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Domain/Primitives/Entity.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Domain/Primitives/AggregateRoot.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Domain/Primitives/ValueObject.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Domain/Primitives/IBusinessRule.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #6
- **Intentional differences:**
  - `CheckRule` returns a Result instead of throwing `BusinessRuleValidationException` (G.1), and a business rule carries an Error instead of a message.
  - An entity is equal to another of the same type with the same ID. The audit dates and the static clock are removed: audit fields are a Unit of Work step (ADR 0003).
  - The aggregate keeps Domain Events in a plain list instead of a `ConcurrentQueue`, raises them through a protected `Raise`, and clears them only through `IAggregateRoot`, implemented explicitly so that it is not on the aggregate's own surface. The de-duplication by event ID and the public add and remove methods are removed.
  - The aggregate exposes a `Version` with no mutator in the domain; the Unit of Work advances it once per changed aggregate. The idea of an aggregate version comes from Ultrabusiness (`a85eacd`, `src/BuildingBlocks/BuildingBlocks.DDD/SeedWork/Primitives/AggregateRoot.cs`), reimplemented with no code taken. Unlike there, raising a Domain Event does not advance it.
  - A value object is a record, so equality is synthesized from its type and members instead of hand-written equality components.

## Domain Event base

- **Kit files:** `src/PouryaKit.Domain/DomainEvent.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Events/DomainEvent.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Events/IDomainEvent.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Messaging/Events/IEvent.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #6
- **Intentional differences:**
  - No MediatR base, and no default interface members: upstream `IEvent.cs:15,20` computed a new ID and time on every read.
  - `IDomainEvent` is folded into the one base record, so every Domain Event gets its identity and time the same way.
  - The ID is a version 7 Guid and the time a UTC `DateTimeOffset`, both from one reading of the clock, instead of the static `IdGenerator` and `SystemClock`. A time given in another offset is stored in UTC.
  - The clock is `DomainClock` (ticket #8, ADR 0005): the `TimeProvider` bound to the running Operation, or `TimeProvider.System`. It is an `AsyncLocal` binding, not a settable static like upstream `SystemClock`, so parallel Operations and tests each keep their own time.
  - The `(Guid, DateTime)` constructor is removed; deserialization restores the `init` properties.

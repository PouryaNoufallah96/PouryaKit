---
status: accepted
---

# One clock, `TimeProvider`, bound to each Operation

The Kit has one time abstraction, `System.TimeProvider`. `PouryaKit.Cqrs` registers `TimeProvider.System` with
`TryAdd`, so a Host or a test registers its own to replace it. `PouryaKit.Domain` reads time through `DomainClock`, an
`AsyncLocal<TimeProvider?>` that falls back to `TimeProvider.System` and is bound with a `Use(TimeProvider)` scope. A
Domain Event reads it once, for both its Id and its `OccurredOn`. The dispatcher binds the container's `TimeProvider`
for each Operation, so the Domain Events of an Operation and the audit stamps the Unit of Work adds later
come from the same clock. A business rule that compares with "now" takes the time as a parameter.

## Considered Options

- **Pass the time into every aggregate method.** Rejected: every Feature that raises a Domain Event would fetch the
  time and thread it through, which lengthens all Feature code for a fact the Kit can supply.
- **Stamp the time in the Unit of Work.** Rejected: a Domain Event's identity and time must be assigned
  once, when it is raised. Stamping at commit changes them after the event exists.
- **A plain static clock**, as in `PouryaNoufallah96/cybernod` at `bae8ac3`,
  `src/BuildingBlocks/BuildingBlocks.Core/Clock/SystemClock.cs`; `kgrzybek/modular-monolith-with-ddd` at `91c8ef2`,
  `src/Modules/Meetings/Domain/SharedKernel/SystemClock.cs`; and Ultrabusiness at `a85eacd`,
  `src/BuildingBlocks/BuildingBlocks.Core/Common/SystemClock.cs`. Rejected: a test that sets it changes the time for
  every test running in parallel. `ClockTests` in `PouryaKit.Cqrs.Tests` fails when
  `DomainClock` is made a plain static field.

## Consequences

- Scopes the dispatcher does not open bind no clock and read the system clock. Notification and Integration Event
  Handlers run in a scope the Kit's shared delivery step opens for each attempt (ADR 0006), not
  through the dispatcher, so that step must bind the container's `TimeProvider` with `DomainClock.Use` around the
  handler.
- Code a test runs directly, outside an Operation, binds a fake clock with `TestClock` from `PouryaKit.Testing`. It is
  created inside the test method: whether an `AsyncLocal` set in an xUnit constructor or fixture reaches the test method
  is unverified, so nothing relies on it. An Operation sent through a Gateway reads the Host's registered
  `TimeProvider`, so a test that needs a fixed time for Operations registers a fake one when it builds the Host.
- Feature code never binds `DomainClock`; a handler that needs the time takes `TimeProvider` from the container.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands; the first consequence is met by another route. Each delivery
attempt runs through the dispatcher, as the Kit's internal `Deliver` command in the scope the delivery step opens
(`src/PouryaKit.Messaging.Wolverine/SubscriptionDelivery.cs`), so the dispatcher binds the container's `TimeProvider`
as it does for every Operation, and no delivery code calls `DomainClock.Use`.

## Amendment (2026-10-07: delayed commands)

The decision above stands; one time the Kit uses is not the Operation's. A delayed command
(`IDelayedCommands.Schedule(command, at)`) is due at an absolute time, which the scheduling handler computes from the
container's `TimeProvider`, as `OpenQuote` does. Wolverine's message store then compares that time with the system
clock (`DateTimeOffset.UtcNow`) on its scheduled-job poll, not with any `TimeProvider`, so a Host that registers a fake
clock fires its delayed commands against real time. A test that needs one to run uses a short real delay
(`DelayedCommandTests`); a fake clock moves the time the Operation records, never when the store runs the command
(ADR 0006, delayed commands amendment).

## Amendment (2026-10-07: recurring commands)

The delayed commands amendment holds for recurring commands too, and more strictly: no Operation computes their times. Each
occurrence of a recurring command falls on a whole multiple of its cadence from the Unix epoch, and the Kit computes the
first and the next occurrence against the system clock (`TimeProvider.System`), the clock the message store fires them
by, not against the container's `TimeProvider`, so a fake clock in a test never moves when an occurrence runs
(`RecurringCommandTests`; ADR 0006, recurring commands amendment).

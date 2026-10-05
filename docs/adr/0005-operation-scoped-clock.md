---
status: accepted
---

# One clock, `TimeProvider`, bound to each Operation

The Kit has one time abstraction, `System.TimeProvider`. `PouryaKit.Cqrs` registers `TimeProvider.System` with
`TryAdd`, so a Host or a test registers its own to replace it. `PouryaKit.Domain` reads time through `DomainClock`, an
`AsyncLocal<TimeProvider?>` that falls back to `TimeProvider.System` and is bound with a `Use(TimeProvider)` scope. A
Domain Event reads it once, for both its Id and its `OccurredOn`. The dispatcher binds the container's `TimeProvider`
for each Operation, so the Domain Events of an Operation and the audit stamps the Unit of Work adds later (ticket #11)
come from the same clock. A business rule that compares with "now" takes the time as a parameter. The owner delegated
this decision on 2026-10-05.

## Considered Options

- **Pass the time into every aggregate method.** Rejected: every Feature that raises a Domain Event would fetch the
  time and thread it through, which lengthens all Feature code for a fact the Kit can supply.
- **Stamp the time in the Unit of Work.** Rejected: story 17 requires a Domain Event's identity and time to be assigned
  once, when it is raised. Stamping at commit changes them after the event exists.
- **A plain static clock** (Cybernod, modular-monolith-with-ddd and Ultrabusiness each have one). Rejected: a test that
  sets it changes the time for every test running in parallel. `ClockTests` in `PouryaKit.Cqrs.Tests` fails when
  `DomainClock` is made a plain static field.

## Consequences

- Scopes the dispatcher does not open bind no clock and read the system clock. Notification and Integration Event
  Handlers (tickets #15 and #16) run in a scope the Kit's shared delivery step opens for each attempt (ADR 0006), not
  through the dispatcher, so that step must bind the container's `TimeProvider` with `DomainClock.Use` around the
  handler.
- Code a test runs directly, outside an Operation, binds a fake clock with `TestClock` from `PouryaKit.Testing`. It is
  created inside the test method: whether an `AsyncLocal` set in an xUnit constructor or fixture reaches the test method
  is unverified, so nothing relies on it. An Operation sent through a Gateway reads the Host's registered
  `TimeProvider`, so a test that needs a fixed time for Operations registers a fake one when it builds the Host.
- Feature code never binds `DomainClock`; a handler that needs the time takes `TimeProvider` from the container.

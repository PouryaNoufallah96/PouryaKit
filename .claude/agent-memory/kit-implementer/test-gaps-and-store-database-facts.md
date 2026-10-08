---
name: test-gaps-and-store-database-facts
description: Ticket 54 test gaps - Wolverine writes envelopes on the DbContext connection (other database fails 42P01, stale store tables silent), limiter catch branch indistinguishable by status, non-Module endpoints in OpenAPI documents, probe commit-then-remove
metadata:
  type: project
---

Verified in the #54 test-gaps package (2026-10-08), WolverineFx 6.46.0, ASP.NET Core 10.0.12; check the merged code first:

- **The Outbox write runs on the Module DbContext's own connection and transaction** (`EfCoreEnvelopeTransaction.PersistIncomingAsync`; the Kit's DbContexts map no Wolverine entities), in the store's schema. So a Module on another database than the store: the Host builds and starts, the first Operation that writes to the Outbox throws `PostgresException 42P01 relation "wolverine.wolverine_incoming_envelopes" does not exist`, nothing commits. If the Module's database still holds a copy of the store's tables (a moved store, or another Host's store), the write succeeds there silently, deliveries run once from the in-memory flush, and the rows stay `Incoming` where no store reads them. Raised with the owner (a build-time cross-check of connections); the probe is commit `3861dc9`.
- **A probe that records a deployment rule**: write it as an output-only Theory, commit it, remove it in the next commit, cite the first sha in `docs/acceptance.md`. Two fresh databases on the AppFixture's container (`create database`), never a new `HostFixture` (it starts its own container) and never the fixture's own database (its live store would absorb writes).
- **The limiter's `BadHttpRequestException` catch cannot be shown by the status**: without it the exception escapes `UseRateLimiter` and `UnexpectedExceptionHandler` still answers 400. The discriminator is accounting: with the catch the call counts in the unresolved partition, so a second such call at permit 1 answers 429.
- A plain `host.MapGet(...)` on a composed sample Host (outside any Module group) appears in every versioned OpenAPI document; the fixture Host has the JWT `Bearer` scheme (from `AddPouryaKitJwtBearer`) beside its default `Test` scheme, so an `AuthorizeAttribute { AuthenticationSchemes = "Bearer" }` shows the policy branch of `BearerSecurity`.
- The Proving Sample maps no idempotent command on PUT or DELETE; the Stub's `CountedNoteEndpoint` maps `CountNoteCommand` on both. Mutating the key read to POST only made the repeat run again (`AssertNothingSentAsync` threw), the POST test stayed green.
- A delayed command's first attempt is read back from the store (no flush for a scheduled envelope), so `DeliveryProbes.ActivitiesOf("billing.expire-quote")` filtered by trace shows the restored parent; wait on the row's `Handled` status, since `WaitForDeliveriesAsync` does not wait for a row not yet due.
- The coordinator may move the integration branch mid-package and ask for a merge before the acceptance step: the merge then sits between the probe commits and the package commit; `git diff <tip> HEAD` stays the package.

**Why:** each decided a #54 test or the item-5 record.
**How to apply:** for any check of the store's connection, any limiter, OpenAPI security or delayed-command trace test, and any recorded probe. Related: [[web-defaults-facts]], [[openapi-versioning-facts]], [[delayed-command-facts]], [[misconfigured-host-facts]].

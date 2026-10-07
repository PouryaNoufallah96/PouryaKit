---
name: idempotency-facts
description: Ticket #42 facts - request id must ride on OperationContext (Gateway scope), ON CONFLICT DO NOTHING vs 23505, pg_stat_activity wait proof, EF1002 and SqlQueryRaw under snake_case, endpoint opt-in ripple
metadata:
  type: project
---

Verified in ticket #42 (2026-10-07), EF Core 10.0.12, Npgsql 10, PostgreSQL 18.6. Check the merged code first:

- **A Gateway opens its own scope** (`GatewayProcessor.RunAsync`: new scope, `BeginOperation(context, module)`), so state put on the HTTP request scope's `Operation` (the `TraceParent` pattern) never reaches the dispatcher. Only the `OperationContext` crosses; that is why `RequestId` is an init property on it (Modularity was outside `allowed_files`). `context with { RequestId = … }` keeps `OperationId` and the private-init `IsTrustedContinuation`.
- **`insert … on conflict do nothing` waits** on another transaction's uncommitted conflicting row, then inserts nothing (it committed) or the row (it rolled back), and leaves the transaction usable. A plain insert raises 23505, which aborts the PostgreSQL transaction (mutation showed `PostgresException 23505` in the race test).
- **Proving a duplicate waits:** `fixture.WaitUntilAsync("exists (select 1 from pg_stat_activity where wait_event_type = 'Lock' and query like 'insert into \"stub\".pouryakit_idempotency_keys%')", …)`. Npgsql rewrites `@p`/`{0}` to `$n`, the prefix stays. With the reservation moved after the handler, the first holds no key yet, so the wait never appears: that is the red.
- **EF1002 is a build error here:** an interpolated string passed straight to `ExecuteSqlRawAsync` or `SqlQueryRaw` is refused. Build the statement through `Func<string, string> sql` and pass the variable, as `EfReceipts` does.
- `Database.SqlQueryRaw<string?>("select response as \"Value\" from … where …", values)` works under `UseSnakeCaseNamingConvention`; a NULL column comes back as a null element.
- `SharedJsonTests` (ArchitectureTests) pins the owners that call `UsePouryaKitJson`; a static field initializer `new JsonSerializerOptions().UsePouryaKitJson()` passes the "own setup" rule and adds its class (`PouryaKit.Cqrs.Dispatcher`) to that list.
- **Opting an existing endpoint in breaks every test that POSTs there:** WebTests, OpenApiTests and ModularMonolith SharedJsonTests needed a key (`IdempotentRequests.PostAsJsonAsync(uri, value, key, ct)`). A malformed-body 400 test passes for the wrong reason without a key. `PersistenceTests` pins every schema's tables and migration history: a new Kit table migration goes there too.
- A missing-header test must post a valid body (existing category), or a mutation that drops the check answers 404, not 201.
- Mutation trap: after `if (x is null) return null;`, a later `if (x is null && …)` makes `x` maybe-null again and the build fails CS8604; mutate without naming `x`. An own-connection mutation can use `((ICloneable)database.GetDbConnection()).Clone()`.
- Runner: `scratchpad/mutate42.py` (one `dotnet test --filter-method` per mutation, file restored in `finally`, files backed up first). 15 mutations took about 8 minutes.

**Why:** these decided the #42 shape or cost a run.
**How to apply:** for #43 (the caching slot in the same `Steps`), any Kit table written in the Unit of Work's transaction, any race test, and any endpoint opt-in. Related: [[persistence-preparation-facts]], [[shared-json-facts]], [[web-aspnetcore-facts]], [[continuation-trust-facts]].

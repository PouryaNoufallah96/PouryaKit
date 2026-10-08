---
name: store-database-check-facts
description: Ticket #63 facts - Npgsql connection-string parsing without connecting, which string the message store really uses, a cross-package record must be public, instance-registered composition checks, build-time checks need no database
metadata:
  type: project
---

Verified in ticket #63 (2026-10-08), Npgsql 10.0.3, WolverineFx 6.46.0; check the merged code first:

- **`new NpgsqlConnectionStringBuilder(s)` does not split `Host=a:5433`**: `Host` stays `"a:5433"` and `Port` reads 5432. The split (`TrySplitHostPort`, last colon unless a bare IPv6 address) runs in the internal `PostProcessAndValidate`, only when a connection opens; a check that parses must split each comma-separated host itself. With `Database` unset Npgsql sends no database parameter, so the server uses the user's name. `PostgresEnvironment` reads only `PGUSER`, `PGPASSWORD`, `PGPASSFILE` and the SSL variables: no `PGHOST`, `PGPORT` or `PGDATABASE` fallback to model. `ilspycmd -p -o <dir> <dll>` decompiles the whole assembly for grepping.
- **The store uses the string `UsePouryaKitWolverine(connectionName)` read at composition** (`PersistMessagesWithPostgresql(connectionString)`), while EF Core resolves a DbContext's `Name=ConnectionStrings:x` from the final `IConfiguration`. A check of "the store's database" compares the captured string with each DbContext's final one; final-vs-final would describe a database Wolverine never uses.
- **A record two packages read needs to be public**: the only `InternalsVisibleTo` between Kit packages is Cqrs's (pinned by `TrustedRestoreTests` and stated in `IRequiresAuthorizer`'s remarks). #63 moved #62's internal `PostgresConnection` to `PouryaKit.Persistence.EFCore` as the public `DbContextConnection`.
- `TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck>(new Check(args)))` works: an instance descriptor's implementation type is the instance's type, so the check can carry composition-time facts in its constructor instead of a marker record.
- **A composition check that opens no connection is tested without databases**: derive the strings from `AppFixture.ConnectionString` with another `Database`, `Port` or `Host` and assert `Build()`'s message; nothing connects while a Kit Host is built, so a check that connected would fail differently. A Host of only `UsePostgres<StubDbContext>` and `UsePouryaKitWolverine` builds under `ValidateOnBuild` with no other check firing.
- **Fix round (review of `99a6d0e`), mirror Npgsql fully or the review finds it**: database is `Database ?? Username` (a record's null == null had made two user-only strings equal); a rooted path or `@…` host is a Unix socket directory (case kept, no port split, separate `Port`); an embedded port is `int.Parse`d (`db:05432` = `Port=5432`), so compare (host, int port) pairs.
- **The DbContext's real connection is in its built options, not the configuration**: `ConfigureDbContext<T>` after `AddDbContext` overrides it. `DbContextOptions<T>` is scoped (a check needs its own `CreateScope()`); read `RelationalOptionsExtension.ConnectionString` or `.Connection`. EF's `NamedConnectionStringResolverBase`: exactly one `=` after the keyword `name`, then `config[key] ?? config["ConnectionStrings:" + key]`. Npgsql EF's `WithDataSource` leaves `ConnectionString` set (stale) and only `NpgsqlOptionsExtension.DataSource` holds the source; it falls back to a DI `NpgsqlDataSource` only with no string, connection or source.
- Mutating away a call to a private helper without an unused-member build error: keep the call, `(Helper(x) is var _ ? alternative : null)`.
- A pass-case test of a check ("two names for one database build") is vacuous with the check off; pair it with a mutation that breaks the comparison (strings compared as written) for its red-once.

**Why:** each decided a #63 design point.
**How to apply:** for any check that reads connection strings, any record shared by two Kit packages, and any build-time check test. Related: [[misconfigured-host-facts]], [[test-gaps-and-store-database-facts]], [[module-seam-and-composition-check-facts]].

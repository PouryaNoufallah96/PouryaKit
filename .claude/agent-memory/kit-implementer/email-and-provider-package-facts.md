---
name: email-and-provider-package-facts
description: Email and MailKit (#124), Papercut in Testcontainers, NuGet audit failures from a provider's transitive packages, the ripple of a new dependency into Stub Notification Handlers, and a one-provider check at start without Cqrs (#147)
metadata:
  type: project
---

## Email and MailKit (#124, 2026-10-17; MailKit and MimeKit 4.18.1, Testcontainers 4.15.0)

- **NuGet audit fails a new provider package on a transitive vulnerability.** MimeKit 4.18.1 asks for `System.Security.Cryptography.Pkcs` 10.0.0; GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7 (published 2026-10-13, fixed in 10.0.13) turn into NU1903/NU1902 errors under TreatWarningsAsErrors. The session approved a direct `PackageReference` to 10.0.13 in `PouryaKit.Email.MailKit` only (not repo-wide transitive pinning), with a PackageGraph row, a section C row and a provenance line saying when to delete it. A new non-`Microsoft.Extensions.*` reference is an owner decision: ask the session with SendMessage, keep working.
- **`System.Formats.Asn1` 10.0.0 is pruned** on net10.0 (framework-provided), so it is not in the restored closure; check `obj/project.assets.json` libraries before writing a closure list.
- **MimeKit's `MailboxAddress.TryParse` accepts a bare local part** (`ada`), and refuses `a b@c`, `<>`, a list. Require `Domain.Length > 0`. Papercut refuses a domainless recipient with `SmtpCommandException: Unrecognized command`.
- **Papercut:** `changemakerstudiosus/papercut-smtp:7.8.0` (latest stable on 2026-10-17; 7.8.1 tags are alphas) works with `Testcontainers.Papercut` 4.15.0 (whose default image is 7.0): `new PapercutBuilder(image)`, `container.Hostname`, `container.SmtpPort` (mapped), `GetBaseAddress()` for HTTP. SMTP 2525 without TLS (`Security=None`), HTTP 8080: `GET /api/messages` gives `{totalMessageCount, messages:[{id, subject, …}]}`, `GET /api/messages/{escaped id}` gives `from[]`, `to[]` (`name`, `address`) and `textBody`; `DELETE /api/messages` clears. An id holds spaces: escape it.
- **A new dependency of a Stub Notification Handler ripples:** the Kit's container validates on build in every environment, so every Host that composes the Stub assembly's handlers must register it: the sample Host (through `AppFixture.ComposeHost`), and the hand-built Hosts in `NotificationTests.An_application_without_modules_…` and `SharedStoreSchemaTests.SharedSchemaFixture`. The first full run shows them; the class alone does not.
- **A test that needs its own mail Host:** an xUnit v3 class fixture may take the assembly fixture in its constructor (`MailFixture(AppFixture app)`): it creates a database on the fixture's container, composes with `AppFixture.ComposeHost(..., arguments)`, starts with `HostFixture.StartHostAsync`, and stops and disposes the Host in `DisposeAsync`. A process-wide static keyed by text counts a handler's attempts whatever Host delivers.

- **Validate every enum option with `Enum.IsDefined`** (#124 review F1, Codex): the configuration binder reads a numeric string (`Security=99`) into an enum, and MailKit's `ComputeDefaultValues` treats an undefined `SecureSocketOptions` as plain `smtp://` without STARTTLS.
- **A retry test must wait for the delivery's end, not for its effects** (#124 review F2): waiting for two mails also passed with a handler that failed every attempt. Wait for the subscription's committed Receipt (`stub.pouryakit_receipts` where `subscription` and `message_id`, the Domain Event's `Id`), then count; red with a handler that fails after every send.

## One provider per Host, checked at start (#147, 2026-10-17)

- **A provider package without Cqrs checks "one provider" as an options validation**: an `IValidateOptions<TProviderOptions>` instance holding the `IServiceCollection` it was registered on (`TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<T>>(new Check(services)))`, deduplicated by type), counting unkeyed descriptors at start, so order does not matter. Never resolve the providers inside it: the provider's factory reads the same options, so validation would re-enter itself.
- **Naming a factory registration:** `descriptor.ImplementationFactory.Method.ReturnType` is the delegate's return type (the interface) for a lambda, but the concrete type for a static method group typed as the implementation (`AddSingleton<IFileStorage>(Create)` with `static S3FileStorage Create(IServiceProvider)`); `ImplementationType` throws on a keyed descriptor, so filter `!IsKeyedService` first.
- **The sample Host registers MailKit inside `ModularMonolithHost.CreateBuilder`**, so `AppFixture.ComposeHost`'s `configure` can only add a second sender after it: an order test builds `Host.CreateApplicationBuilder()` by hand (fully qualified in `ModularMonolith.Tests`).
- **Mutation by deleting a registration line fails the build** with IDE0005 (its `using` becomes unused): switch the check off with a runtime-true predicate (`providers.Count >= 0`) instead.
- **Removing a test an older acceptance row names** fails `check-docs.py` (`names no test`) unless it goes in `REMOVED_TESTS` in the script; a bare backticked method name without its class passes, so the row can say the test was removed and point to the replacement.

**Why:** each cost a build or a full run in #124 or #147.
**How to apply:** for the next provider package (Storage, Localization, AI), any new container module, and any change to what a Stub Notification Handler needs. Related: [[templates-packaging-and-publication-facts]], [[dotnet-and-test-conventions]], [[docs-verification-tools]].

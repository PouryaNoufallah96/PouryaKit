# PouryaKit.Email.MailKit provenance

Adopted implementations in `PouryaKit.Email.MailKit`. Format and rules: [README](README.md).

The options validation in `src/PouryaKit.Email.MailKit/MailKitServiceCollectionExtensions.cs` follows the Kit's own options pattern (`AddOptions`, `Validate`, `ValidateOnStart`, as `PouryaKitJwtBearerOptions` does), so it has no record here. Since ticket #147 the package also references `PouryaKit.Cqrs`, for its one-provider composition check, since the PR #143 audit Cqrs's `OneProviderCheck<IEmailSender>` (`src/PouryaKit.Cqrs/Composition/OneProviderCheck.cs`).

## Package dependencies

Checked on nuget.org on 2026-10-17 (ticket #124); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `MailKit` 4.18.2, `MIT`, the latest stable, with a `net10.0` asset (4.18.1 until ticket #167, 2026-10-24). It brings `MimeKit` 4.18.2 and `BouncyCastle.Cryptography` 2.7.0, both `MIT`; `System.Formats.Asn1` 10.0.13, which the shared framework supplies on `net10.0`; and `System.Security.Cryptography.Pkcs` 10.0.13, through MimeKit.
- `Microsoft.Extensions.Options` 10.0.13, `MIT`, the Kit's line of `Microsoft.Extensions.*` 10.0.13, referenced under the standing rule of plan section C for the options the Host binds and the Kit validates at start. It brings `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Primitives` 10.0.13, both `MIT`.
- `System.Security.Cryptography.Pkcs` 10.0.13, `MIT`, with no dependency on `net10.0`: a direct reference that raises the 10.0.0 MimeKit asks for to the first release outside CVE-2026-84008 (GHSA-846f-f764-76mx, high) and CVE-2026-70162 (GHSA-2fmv-rv2c-59x7, moderate), both affecting 10.0.0 to 10.0.12 and published on 2026-10-13. Without it NuGet's audit fails the build (NU1902, NU1903). A reference outside plan section C's standing rule, taken by the session for #124 on 2026-10-17, approved by the owner on 2026-10-17 (spec #115); approved by the owner on 2026-10-17 (spec #115, the session 2 close). Delete this reference once a MailKit release depends on a MimeKit that asks for 10.0.13 or later. *Deleted on 2026-10-24 (ticket #167): `MailKit` 4.18.2 depends on `MimeKit` 4.18.2, which asks for 10.0.13 on `net10.0`, so the package references it no longer and the closure keeps it at 10.0.13.*

Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## MailKit email sender

- **Kit files:** `src/PouryaKit.Email.MailKit/MailKitEmailSender.cs`, `src/PouryaKit.Email.MailKit/PouryaKitMailKitOptions.cs`, `src/PouryaKit.Email.MailKit/MailKitServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/mehdihadeli/CloudNativeKit
- **Commit:** e81d65e6376008cb001fdbd886086dc6e84790c6
- **Upstream paths:** `src/CloudNativeKit.Email/MailKitEmailSender.cs`, `src/CloudNativeKit.Email/Options/MailKitOptions.cs`, `src/CloudNativeKit.Email/DependencyInjectionExtensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #124
- **Intentional differences:**
  - Kept: a `MimeMessage` built from the message, then `ConnectAsync`, `AuthenticateAsync`, `SendAsync` and `DisconnectAsync(true)` on an `SmtpClient` of the send's own (`MailKitEmailSender.cs:27-36`), a connection per send from one registration. Spec #115 let the ticket choose between that and one connected client guarded for one send at a time; MailKit's `SmtpClient` documents reusing a connection as an optimization for many messages over a short period, and shares one between threads only under a lock, while a Notification Handler's sends are sporadic and concurrent. What the plan rejects, a client created per send from configuration read per call (`SendGridEmailSender.cs:22`), is not taken: the sender is one singleton over options validated once.
  - No `catch` that logs and returns: upstream swallows every exception (`MailKitEmailSender.cs:47-50`), so a failed send looks sent. The Kit lets a connection, login or refusal failure throw, so the delivery retries it, and returns `EmailErrors.InvalidAddress` for a recipient that is not a mailbox address with a domain.
  - `StartTls` is a default that can be changed (`PouryaKitMailKitOptions.Security`), where upstream always uses `SecureSocketOptions.StartTls`; authentication runs only when a user name is set, where upstream always authenticates.
  - One provider per package and no provider switch: upstream's `EmailProvider` enum chooses between MailKit and SendGrid at registration, and its `EmailOptions` carries both providers' settings and an unused `Enable` flag.
  - The Host binds the options from configuration and the Kit validates them at start; upstream binds them in its registration call and calls `ValidateDataAnnotations` with no annotations on `MailKitOptions`, whose `required` members are not checked at bind time.
  - No log of the sent mail's body: upstream logs every mail's content at Information.

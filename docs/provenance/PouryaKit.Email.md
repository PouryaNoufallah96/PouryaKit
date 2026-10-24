# PouryaKit.Email provenance

Adopted implementations in `PouryaKit.Email`. Format and rules: [README](README.md).

`EmailErrors` is new Kit code: the reference contract returns nothing, so it has no Error to adopt.

## Package dependencies

None. The package references `PouryaKit.Domain` only, and `PackageGraphTests` keeps every package out of its closure.

Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Email sender contract

- **Kit files:** `src/PouryaKit.Email/IEmailSender.cs`, `src/PouryaKit.Email/EmailMessage.cs`
- **Upstream:** https://github.com/mehdihadeli/CloudNativeKit
- **Commit:** e81d65e6376008cb001fdbd886086dc6e84790c6
- **Upstream paths:** `src/CloudNativeKit.Email/IEmailSender.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #124
- **Intentional differences:**
  - Kept: one sender interface with one send method, and a message of recipient, subject and body.
  - The send takes a `CancellationToken` and returns a `Result` (plan I.3), where upstream's `SendAsync(EmailObject)` takes neither and returns a bare `Task`.
  - `EmailMessage` is a positional record with no sender of its own: the sender address is the provider's configured `From`, where upstream's `EmailObject` has an optional `SenderEmail`.
  - The body is plain text, where upstream sends it as HTML.
  - Upstream validates its arguments with `CloudNativeKit.Core` guard extensions in the message's constructor; the Kit's provider checks the recipient when it sends and fails with `EmailErrors.InvalidAddress`.

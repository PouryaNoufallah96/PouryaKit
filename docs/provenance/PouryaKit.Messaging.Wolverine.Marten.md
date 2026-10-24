# PouryaKit.Messaging.Wolverine.Marten provenance

Adopted implementations in `PouryaKit.Messaging.Wolverine.Marten`. Format and rules: [README](README.md).

The package has no adopted implementation. Its Outbox, `src/PouryaKit.Messaging.Wolverine.Marten/MartenStreamOutbox.cs`, writes through `OutboxWriter` of `PouryaKit.Messaging.Wolverine` into Wolverine's own `MartenOutbox`, its Receipts run the EF package's statements, `ReceiptStatements`, on the Marten Unit of Work's transaction, and its registration calls Marten's `IntegrateWithWolverine` and the Kit's `UsePouryaKitMessageStore`: the shape Spike #120 proved and ADR 0011 records (`spikes/marten-two-store/` at `d03cc740`, tag `spike/marten-two-store`, not merged; ticket #131).

## Package dependencies

Checked on nuget.org and in the restored nuspecs on 2026-10-18 (ticket #131). None is reciprocal or commercial (ADR 0002), and `LicenceTests` checks the whole closure.

- `WolverineFx.Marten` 6.48.3, `MIT`, owner decision M1, the Kit's WolverineFx line (6.51.0 is current); it depends on `Marten` 9.46.0 and brings `WolverineFx.Postgresql` 6.48.3, `MIT`, the PostgreSQL message store Marten's integration builds. Beside `PouryaKit.Messaging.Wolverine.EFCore`'s closure it adds only `Marten`, `Weasel.Storage` and `Microsoft.IO.RecyclableMemoryStream`, as ADR 0011's comparison of the restored closures records; the `Microsoft.Extensions.Caching.Abstractions`, `Microsoft.Extensions.Caching.Memory` and `Microsoft.Extensions.Logging` 10.0.0 it declares are already in that closure, at 10.0.12.

`PouryaKit.Messaging.Wolverine.Marten` also references `PouryaKit.Messaging.Wolverine` and `PouryaKit.Persistence.Marten`, as plan section C lists since ticket #131. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

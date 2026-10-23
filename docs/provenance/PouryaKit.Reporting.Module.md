# PouryaKit.Reporting.Module provenance

Adopted implementations in `PouryaKit.Reporting.Module`. Format and rules: [README](README.md).

The Module's Composition Root, its two Host ports, its options, its endpoints, its migrations and its retention are new Kit code, built to spec #115's Reporting decision (E2, E3) on the Kit's Modularity, delayed and recurring commands, renderer and storage contract, so they have no record. The claim and settlement of a Report job follow one design from a reference project, recorded below; no code was taken.

## Package dependencies

None. The package references Kit packages only: `PouryaKit.Modularity`, `PouryaKit.Persistence.Postgres`, `PouryaKit.Persistence.Dapper`, `PouryaKit.Messaging.Wolverine.EFCore`, `PouryaKit.Validation`, `PouryaKit.Security`, `PouryaKit.Web`, `PouryaKit.Reporting` and `PouryaKit.Storage`, with the ASP.NET Core framework reference for its endpoints; their third-party packages are recorded in their own files. Checked on 2026-10-18 (ticket #140): its restored closure holds no package that `LicenceTests` refuses, and no storage provider or SDK.

## Fenced claims and settlements of a Report job

- **Kit files:** `src/PouryaKit.Reporting.Module/Jobs/ReportJob.cs`, `src/PouryaKit.Reporting.Module/Features/ClaimReportJob/ClaimReportJobCommandHandler.cs`, `src/PouryaKit.Reporting.Module/Features/RenderReport/RenderReportCommandHandler.cs`, `src/PouryaKit.Reporting.Module/Features/ApplyRetention/ApplyRetentionCommandHandler.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd4db5e1af9b8349d49350be12397f24402 (the plan's I.3 pin)
- **Upstream paths:** `src/BuildingBlocks/MainArchitecture/Core/MainArchitecture.Domain/Aggregates/ReportJobAggregate/Entities/ReportJob.cs` (`TryClaimForGeneration`, `MarkCompleted`, `MarkFailed`, `MarkExpired` and the private `CanClaimForGeneration` and `CanSettleAttempt`: a claim stamps an attempt id and the claim time, only a stale claim is taken again, only the claiming attempt settles, and an expiry refuses a job whose claim is fresh); read at the checkout's commit `766bc57741ca6b78171c9c83e56bc1a14f2dc430`, where the cited file is byte-identical to the pin `a85eacd` (`git diff` empty, checked 2026-10-24)
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #140
- **Intentional differences:**
  - The attempt is a number each claim takes in turn, and every claim, settlement and expiry is saved under the aggregate's `Version`, the concurrency token of every Kit aggregate, so of two that read one version only the first to commit stands; upstream stamps a `Guid` attempt id.
  - A stale claim is taken over once, by the takeover the first claim schedules as a delayed command; upstream's at-least-once poll claims a stale job again whenever it finds one.
  - Kept: each attempt stores its file under a key of its own, `reports/{job id}/{attempt}`, as upstream's attempt-qualified key does, so a stale attempt's late upload overwrites nothing the job serves.
  - Generation and retention run as the Kit's delayed and recurring commands, through the Unit of Work, and no Quartz job; retention deletes a job's files only once a committed expiry stopped it serving them, then removes the job.
  - The job stores its requester's Actor and tenant and answers only them, or an Actor the Host grants the tenant's jobs; upstream's job carries an owner, a company branch and a requesting user, and raises a Domain Event on each transition, which the Kit's job does not.

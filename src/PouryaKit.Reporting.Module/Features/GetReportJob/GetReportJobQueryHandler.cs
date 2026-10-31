using Dapper;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Security;
using PouryaKit.Storage;

namespace PouryaKit.Reporting.Module;

// A job is read only within the Operation's tenant, the one the lease bound, and only by its requester or an Actor the
// Host grants every job of the tenant; anyone else is answered as for a job that does not exist. The link is signed
// only after both checks pass, and never past the job's expiry, after which the retention may delete its file. A job
// read at or past its expiry reads as Expired, with no link, whatever its row still says: the retention stores that
// state for every such job, for one whose attempt holds a fresh claim once the claim goes stale or the attempt
// settles, and the job never serves a file again.
internal sealed class GetReportJobQueryHandler(
    IReadConnectionFactory connections,
    IActorPermissions permissions,
    IFileStorage files,
    OperationContext context,
    TimeProvider time,
    IOptions<ReportOptions> options
) : QueryHandler<GetReportJobQuery, ReportJobDto>
{
    public override async Task<Result<ReportJobDto>> Handle(
        GetReportJobQuery query,
        CancellationToken cancellationToken
    )
    {
        JobRow? job;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            job = await connection.QuerySingleOrDefaultAsync<JobRow>(
                new CommandDefinition(
                    "select id, kind, format, state, requested_by as requestedby, requested_by_kind as requestedbykind, file_key as filekey, failure, expires_at as expiresat from reports.report_jobs where id = @Id and coalesce(tenant, '') = current_setting('pouryakit.tenant')",
                    new { Id = query.JobId },
                    cancellationToken: cancellationToken
                )
            );
        }

        if (
            job is null
            || (
                !(job.RequestedBy == context.Actor.Id && job.RequestedByKind == context.Actor.Kind)
                && !await permissions.HasPermissionAsync(
                    context,
                    ReportModule.ReadTenantJobsPermission,
                    cancellationToken
                )
            )
        )
        {
            return ReportJobErrors.NotFound;
        }

        var expiresAt = new DateTimeOffset(job.ExpiresAt, TimeSpan.Zero);
        var left = expiresAt - time.GetUtcNow();
        var state = left > TimeSpan.Zero ? job.State : ReportJobState.Expired;
        Uri? link = null;
        if (state == ReportJobState.Completed)
        {
            var lifetime = left < options.Value.LinkLifetime ? left : options.Value.LinkLifetime;
            var signed = await files.GetPresignedUrlAsync(
                job.FileKey!,
                lifetime,
                cancellationToken
            );
            if (signed.IsFailure)
            {
                return signed.Errors;
            }

            link = signed.Value;
        }

        return new ReportJobDto(job.Id, job.Kind, job.Format, state, link, job.Failure, expiresAt);
    }

    // Read by Dapper, which sets each property from its column and parses an enum from its text.
    private sealed class JobRow
    {
        public Guid Id { get; init; }

        public string Kind { get; init; } = "";

        public ReportFormat Format { get; init; }

        public ReportJobState State { get; init; }

        public string RequestedBy { get; init; } = "";

        public ActorKind RequestedByKind { get; init; }

        public string? FileKey { get; init; }

        public string? Failure { get; init; }

        public DateTime ExpiresAt { get; init; }
    }
}

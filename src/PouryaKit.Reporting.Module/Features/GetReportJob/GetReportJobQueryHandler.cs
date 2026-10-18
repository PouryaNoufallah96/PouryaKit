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
// only after both checks pass.
internal sealed class GetReportJobQueryHandler(
    IReadConnectionFactory connections,
    IActorPermissions permissions,
    IFileStorage files,
    OperationContext context,
    IOptions<ReportOptions> options
) : QueryHandler<GetReportJobQuery, ReportJobDto>
{
    public override async Task<Result<ReportJobDto>> Handle(GetReportJobQuery query, CancellationToken cancellationToken)
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
                && !await permissions.HasPermissionAsync(context, ReportModule.ReadTenantJobsPermission, cancellationToken)
            )
        )
        {
            return ReportJobErrors.NotFound;
        }

        Uri? link = null;
        if (job.State == ReportJobState.Completed)
        {
            var signed = await files.GetPresignedUrlAsync(job.FileKey!, options.Value.LinkLifetime, cancellationToken);
            if (signed.IsFailure)
            {
                return signed.Errors;
            }

            link = signed.Value;
        }

        return new ReportJobDto(job.Id, job.Kind, job.Format, job.State, link, job.Failure, new DateTimeOffset(job.ExpiresAt, TimeSpan.Zero));
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

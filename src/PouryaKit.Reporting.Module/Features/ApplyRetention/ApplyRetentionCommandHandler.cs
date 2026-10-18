using Ardalis.Specification;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Storage;

namespace PouryaKit.Reporting.Module;

// Retention in two steps, so that it never deletes a file a job serves: an occurrence expires the jobs past their
// expiry, and only a later occurrence, once that expiry is committed and the jobs serve nothing, deletes every file
// their attempts may have stored, then the jobs. When a settlement commits after this occurrence read its job, the job's
// version refuses the expiry and the occurrence rolls back; its retry reads the settled job. When the occurrence rolls
// back after deleting files, the next one deletes the same keys again, which succeeds for a key already gone. Each step
// takes a batch, oldest expiry first, so a backlog drains over occurrences.
internal sealed class ApplyRetentionCommandHandler(
    IRepository<ReportJob, ReportJobId> jobs,
    IFileStorage files,
    TimeProvider time,
    IOptions<PouryaKitReportOptions> options
) : CommandHandler<ApplyRetentionCommand>
{
    private const int Batch = 100;

    public override async Task<Result> Handle(ApplyRetentionCommand command, CancellationToken cancellationToken)
    {
        foreach (var expired in await jobs.ListAsync(new ExpiredJobs(), cancellationToken))
        {
            foreach (var key in expired.FileKeys)
            {
                var deleted = await files.DeleteAsync(key, cancellationToken);
                if (deleted.IsFailure)
                {
                    return deleted;
                }
            }

            jobs.Remove(expired);
        }

        var now = time.GetUtcNow();
        foreach (var job in await jobs.ListAsync(new JobsPastExpiry(now), cancellationToken))
        {
            job.TryExpire(now, options.Value.ClaimTimeout);
        }

        return Result.Success();
    }

    private sealed class ExpiredJobs : Specification<ReportJob>
    {
        public ExpiredJobs() => Query.Where(job => job.State == ReportJobState.Expired).OrderBy(job => job.ExpiresAt).Take(Batch);
    }

    private sealed class JobsPastExpiry : Specification<ReportJob>
    {
        public JobsPastExpiry(DateTimeOffset now) =>
            Query.Where(job => job.State != ReportJobState.Expired && job.ExpiresAt <= now).OrderBy(job => job.ExpiresAt).Take(Batch);
    }
}

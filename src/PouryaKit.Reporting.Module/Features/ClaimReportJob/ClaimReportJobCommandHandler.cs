using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace PouryaKit.Reporting.Module;

// The claim commits on its own, before its attempt renders, so a later claim sees it and the version every save checks
// fences the two: of two claims of one job that read it at one version, the second to commit is refused, rolls back and,
// retried, finds the claim taken. The first claim also schedules the one takeover, due when its claim goes stale: a
// takeover schedules none, so a job is taken over at most once. A claim that finds nothing to take changes nothing and
// succeeds, since its delivery would otherwise be retried and set aside as a dead letter for a normal outcome.
internal sealed class ClaimReportJobCommandHandler(
    IRepository<ReportJob, ReportJobId> jobs,
    IDelayedCommands later,
    TimeProvider time,
    IOptions<PouryaKitReportOptions> options
) : CommandHandler<ClaimReportJobCommand>
{
    public override async Task<Result> Handle(ClaimReportJobCommand command, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(new ReportJobId(command.JobId), cancellationToken);
        var now = time.GetUtcNow();
        if (job is null || !job.TryClaim(command.Attempt, now, options.Value.ClaimTimeout))
        {
            return Result.Success();
        }

        later.Schedule(new RenderReportCommand(command.JobId, command.Attempt), now);
        if (command.Attempt == 1)
        {
            later.Schedule(new ClaimReportJobCommand(command.JobId, Attempt: 2), now + options.Value.ClaimTimeout);
        }

        return Result.Success();
    }
}

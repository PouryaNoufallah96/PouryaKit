using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace PouryaKit.Reporting.Module;

// The job and its first claim commit together, or neither does; the claim then runs in the background as a continuation
// of this Operation, with its Actor and tenant.
internal sealed class RequestReportCommandHandler(
    IRepository<ReportJob, ReportJobId> jobs,
    IDelayedCommands later,
    OperationContext context,
    TimeProvider time,
    IOptions<ReportOptions> options
) : CommandHandler<RequestReportCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(
        RequestReportCommand command,
        CancellationToken cancellationToken
    )
    {
        var now = time.GetUtcNow();
        var job = ReportJob.Request(
            new ReportJobId(Guid.CreateVersion7()),
            command.ToRequest(),
            context,
            now,
            options.Value.Retention
        );
        await jobs.AddAsync(job, cancellationToken);
        later.Schedule(new ClaimReportJobCommand(job.Id.Value, Attempt: 1), now);
        return job.Id.Value;
    }
}

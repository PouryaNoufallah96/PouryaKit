using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Storage;

namespace PouryaKit.Reporting.Module;

// Store, then record: the file is stored under this attempt's own key before the settlement that publishes the key
// commits. An attempt that no longer holds the claim when it starts stores nothing; one taken over while it renders
// stores its file under its own key, which the job never serves, and its settlement is refused when it saves, since
// the takeover changed the job's version after this Operation read it: the attempt rolls back and, retried, finds the
// claim taken. A file stored by an attempt that never settles stays until retention removes the job's files. A layout
// or rendering failure fails the job; a failed upload fails the delivery, which is retried.
internal sealed class RenderReportCommandHandler(
    IRepository<ReportJob, ReportJobId> jobs,
    IReportLayouts layouts,
    IReportRenderer renderer,
    IFileStorage files,
    OperationContext context
) : CommandHandler<RenderReportCommand>
{
    public override async Task<Result> Handle(RenderReportCommand command, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(new ReportJobId(command.JobId), cancellationToken);
        if (job is null || !job.IsClaimedBy(command.Attempt))
        {
            return Result.Success();
        }

        var layout = await layouts.LayOutAsync(context, job.ToRequest(), cancellationToken);
        Result<RenderedReport> rendered = layout.IsSuccess ? renderer.Render(layout.Value, job.Format) : layout.Errors;
        if (rendered.IsFailure)
        {
            job.Fail(command.Attempt, rendered.Errors[0]);
            return Result.Success();
        }

        var key = job.FileKeyOf(command.Attempt);
        var stored = await files.UploadAsync(
            key,
            new MemoryStream(rendered.Value.Content, writable: false),
            rendered.Value.ContentType,
            cancellationToken
        );
        if (stored.IsFailure)
        {
            return stored;
        }

        job.Complete(command.Attempt, key);
        return Result.Success();
    }
}

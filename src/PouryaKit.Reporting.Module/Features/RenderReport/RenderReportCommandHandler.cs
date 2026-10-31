using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Storage;

namespace PouryaKit.Reporting.Module;

// Store, then record: the file is stored under this attempt's own key before the settlement that publishes the key
// commits. An attempt that no longer holds the claim when it starts stores nothing, and deletes its own key: one taken
// over, expired or removed while it renders stores its file under that key, which the job never serves, and its
// settlement is refused when it saves, since the job's version changed or its row is gone after this Operation read it;
// the attempt rolls back and, retried, finds the claim lost and removes the file it stored. It never deletes the key the
// job publishes, which an attempt that settled published with its Receipt, after which it runs no more. A layout or
// rendering failure fails the job; a failed upload or deletion fails the delivery, which is retried.
internal sealed class RenderReportCommandHandler(
    IRepository<ReportJob, ReportJobId> jobs,
    IReportLayouts layouts,
    IReportRenderer renderer,
    IFileStorage files,
    OperationContext context
) : CommandHandler<RenderReportCommand>
{
    public override async Task<Result> Handle(
        RenderReportCommand command,
        CancellationToken cancellationToken
    )
    {
        var id = new ReportJobId(command.JobId);
        var key = ReportJob.FileKeyOf(id, command.Attempt);
        var job = await jobs.FindAsync(id, cancellationToken);
        if (job is null || !job.IsClaimedBy(command.Attempt))
        {
            return job?.FileKey == key
                ? Result.Success()
                : await files.DeleteAsync(key, cancellationToken);
        }

        var layout = await layouts.LayOutAsync(context, job.ToRequest(), cancellationToken);
        Result<RenderedReport> rendered = layout.IsSuccess
            ? renderer.Render(layout.Value, job.Format)
            : layout.Errors;
        if (rendered.IsFailure)
        {
            job.Fail(command.Attempt, rendered.Errors[0]);
            return Result.Success();
        }

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

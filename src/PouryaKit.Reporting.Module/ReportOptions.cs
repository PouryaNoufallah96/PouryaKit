namespace PouryaKit.Reporting.Module;

// The Module's own settings, which it binds from the Host's configuration section PouryaKit:Reports and validates when
// the Host starts.
internal sealed class ReportOptions
{
    public const string Section = "PouryaKit:Reports";

    // How long after its request a job and its file are kept.
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    // The cadence of the retention.
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    // How long an attempt holds its claim before the one takeover may take the job: longer than the slowest report takes
    // to lay out, render and store.
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(10);

    // How long a download link works, at most what the Host's file storage provider signs a link for.
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

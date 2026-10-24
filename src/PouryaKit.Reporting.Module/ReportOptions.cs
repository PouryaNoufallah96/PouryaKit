namespace PouryaKit.Reporting.Module;

// The Module's own settings, which it binds from the Host's configuration section PouryaKit:Reports and validates when
// the Host starts.
internal sealed class ReportOptions
{
    public const string Section = "PouryaKit:Reports";

    // The longest Retention, CleanupInterval and ClaimTimeout: each sets a time at most that long after now (a job's
    // expiry, its takeover's due time, the retention's next occurrence), which stays far from DateTimeOffset.MaxValue.
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(36500);

    // The longest link both bundled providers sign, S3FileStorage and AzureBlobFileStorage: each refuses a longer one.
    public static readonly TimeSpan MaxLinkLifetime = TimeSpan.FromDays(7);

    // How long after its request a job and its file are kept.
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    // The cadence of the retention.
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    // How long an attempt holds its claim before the one takeover may take the job: longer than the slowest report takes
    // to lay out, render and store.
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(10);

    // How long a download link works, at most what the Host's file storage provider signs a link for; a link is never
    // signed past its job's expiry, after which the retention may delete the file.
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

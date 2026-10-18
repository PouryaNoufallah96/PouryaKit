namespace PouryaKit.Reporting.Module;

/// <summary>
/// How the report Module keeps and serves report jobs, bound from configuration by the Host, for example
/// <c>Configure&lt;PouryaKitReportOptions&gt;(configuration.GetSection("PouryaKit:Reports"))</c>. The Host fails to start
/// when any of them is not longer than zero.
/// </summary>
public sealed class PouryaKitReportOptions
{
    /// <summary>How long after its request a job and its file are kept: seven days unless set.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How often the retention removes expired jobs and their files, a recurring command of the Module: every hour unless
    /// set.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a generation attempt holds its claim before another attempt may take the job over, once: ten minutes
    /// unless set. Set it longer than the slowest report takes to lay out, render and store.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a download link the job endpoint gives the requester works: fifteen minutes unless set, and at most what
    /// the Host's file storage provider signs a link for, seven days for S3 and Azure Blob.
    /// </summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

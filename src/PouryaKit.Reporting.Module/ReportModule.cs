using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Validation;
using PouryaKit.Web;

namespace PouryaKit.Reporting.Module;

/// <summary>
/// The report Module's Composition Root, which a Host adds once with <c>modules.Add&lt;ReportModule&gt;()</c>. The Module
/// stores report jobs in the schema <c>reports</c>, generates each in the background through the Host's
/// <see cref="IReportLayouts"/>, the renderer and the Host's one file storage provider, removes expired jobs and their
/// files, and maps <c>POST /reports</c> and <c>GET /reports/{jobId}</c>. The Host registers its
/// <see cref="IReportRequestAuthorizer"/>, its <see cref="IReportLayouts"/>, an <c>IFileStorage</c> provider and an
/// <c>IActorPermissions</c>, and its container's validation fails the build without any of them. The Module reads its
/// settings from the Host's configuration section <c>PouryaKit:Reports</c>: <c>Retention</c>, how long after its
/// request a job and its file are kept (seven days); <c>CleanupInterval</c>, the retention's cadence (an hour);
/// <c>ClaimTimeout</c>, how long an attempt holds its claim before the one takeover (ten minutes); and
/// <c>LinkLifetime</c>, how long a download link works (fifteen minutes), never past the job's expiry. The Host fails
/// to start unless each is longer than zero, the first three at most 36500 days and <c>LinkLifetime</c> at most seven days.
/// </summary>
public sealed class ReportModule : IModuleDefinition
{
    /// <summary>
    /// The connection string the Module stores over, <c>app</c>, the name the Kit's templates and Proving Sample give the
    /// Host's database: a Composition Root receives no configuration, so the name is the Module's. It must name the
    /// message store's database, since the Module's delayed and recurring commands commit through its Outbox.
    /// </summary>
    public const string ConnectionName = "app";

    /// <summary>
    /// The permission that lets an Actor read every report job of its own tenant, not only the jobs it requested, as
    /// the Host's <c>IActorPermissions</c> grants it.
    /// </summary>
    public const string ReadTenantJobsPermission = "reports.jobs.read-tenant";

    /// <summary>The system Actor every occurrence of the Module's retention runs as.</summary>
    public const string RetentionActor = "reports.retention";

    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.UsePouryaKitPostgres<ReportDbContext>(ConnectionName, schema: "reports");
        module.AddPouryaKitDapper(ConnectionName);
        module.AddPouryaKitCqrs();
        module.AddPouryaKitValidation();
        module.AddPouryaKitDelayedCommand<ClaimReportJobCommand>("reports.claim");
        module.AddPouryaKitDelayedCommand<RenderReportCommand>("reports.render");
        module
            .AddPouryaKitRecurringCommand(
                "reports.retention",
                RetentionActor,
                occurrence => new ApplyRetentionCommand(occurrence)
            )
            .Configure<IOptions<ReportOptions>>(
                (retention, reports) => retention.Every = reports.Value.CleanupInterval
            );
        module
            .AddOptions<ReportOptions>()
            .BindConfiguration(ReportOptions.Section)
            .Validate(
                reports =>
                    reports.Retention > TimeSpan.Zero
                    && reports.Retention <= ReportOptions.MaxDuration
                    && reports.CleanupInterval > TimeSpan.Zero
                    && reports.CleanupInterval <= ReportOptions.MaxDuration
                    && reports.ClaimTimeout > TimeSpan.Zero
                    && reports.ClaimTimeout <= ReportOptions.MaxDuration
                    && reports.LinkLifetime > TimeSpan.Zero
                    && reports.LinkLifetime <= ReportOptions.MaxLinkLifetime,
                $"{ReportOptions.Section}: Retention, CleanupInterval and ClaimTimeout must each be positive and at most {ReportOptions.MaxDuration}, and LinkLifetime positive and at most {ReportOptions.MaxLinkLifetime}, the longest link S3 and Azure Blob sign."
            )
            .ValidateOnStart();
        module.TryAddSingleton<IReportRenderer, ReportRenderer>();
        module.AddPouryaKitEndpoints("/reports");
    }
}

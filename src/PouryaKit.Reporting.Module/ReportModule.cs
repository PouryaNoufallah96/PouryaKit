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
/// <c>IActorPermissions</c>, and its container's validation fails the build without any of them.
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
            .AddPouryaKitRecurringCommand("reports.retention", RetentionActor, occurrence => new ApplyRetentionCommand(occurrence))
            .Configure<IOptions<PouryaKitReportOptions>>((retention, reports) => retention.Every = reports.Value.CleanupInterval);
        module
            .AddOptions<PouryaKitReportOptions>()
            .Validate(
                reports => reports.Retention > TimeSpan.Zero
                    && reports.CleanupInterval > TimeSpan.Zero
                    && reports.ClaimTimeout > TimeSpan.Zero
                    && reports.LinkLifetime > TimeSpan.Zero,
                "PouryaKitReportOptions.Retention, CleanupInterval, ClaimTimeout and LinkLifetime are each longer than zero."
            )
            .ValidateOnStart();
        module.TryAddSingleton<IReportRenderer, ReportRenderer>();
        module.AddPouryaKitEndpoints("/reports");
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PouryaKit.Observability;
using PouryaKit.Web;
using Serilog;

namespace Microsoft.Extensions.Hosting;

// The service defaults of a Host built on PouryaKit, shaped like .NET Aspire's ServiceDefaults template: OpenTelemetry
// that exports the Kit's activities and metrics, health checks, service discovery and the standard HTTP resilience, and
// Serilog when the Host wants it. Template content, not a Kit package: each recipe is optional, and a Host takes or
// changes them in its own copy.
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string LiveTag = "live";
    private const string OtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        public TBuilder AddServiceDefaults()
        {
            builder.ConfigureOpenTelemetry();
            builder.AddDefaultHealthChecks();
            builder.Services.AddServiceDiscovery();
            // One resilience pipeline for every client, the Kit's standard handler that retries safe methods only. A
            // client that needs other limits, or replays writes, replaces it: RemoveAllResilienceHandlers(), then
            // AddPouryaKitResilience(...). Registered on top of this one it would stack a second pipeline.
            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                http.AddPouryaKitResilience();
                http.AddServiceDiscovery();
            });
            return builder;
        }

        // The Kit's source and meter (OperationTelemetry.SourceName) beside ASP.NET Core, HttpClient and the runtime;
        // add "Wolverine" to the sources to export Wolverine's spans too. OTLP export only when the endpoint is set.
        public TBuilder ConfigureOpenTelemetry()
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });
            builder
                .Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                    metrics
                        .AddMeter(OperationTelemetry.SourceName)
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                )
                .WithTracing(tracing =>
                    tracing
                        .AddSource(builder.Environment.ApplicationName, OperationTelemetry.SourceName)
                        .AddAspNetCoreInstrumentation(aspNetCore =>
                            aspNetCore.Filter = context =>
                                !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                                && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                        )
                        .AddHttpClientInstrumentation()
                );
            if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpoint]))
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }

            return builder;
        }

        // Liveness asks nothing outside the process; readiness also asks each database the Host's DbContexts use.
        public TBuilder AddDefaultHealthChecks()
        {
            builder
                .Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag])
                .AddCheck<DbContextsHealthCheck>("databases");
            return builder;
        }

        // Serilog in place of the logging providers: a console sink, and an OTLP sink when the endpoint is set, which
        // takes over from the OpenTelemetry logging provider above, since providers no longer receive anything. Sinks
        // registered as ILogEventSink services are added too, so a test can observe the Host's logs.
        public TBuilder AddSerilogDefaults()
        {
            builder.Services.AddSerilog(
                (services, logger) =>
                {
                    logger
                        .ReadFrom.Configuration(builder.Configuration)
                        .ReadFrom.Services(services)
                        .Enrich.FromLogContext()
                        .WriteTo.Console();
                    if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpoint]))
                    {
                        logger.WriteTo.OpenTelemetry(_ => { }, name => builder.Configuration[name]);
                    }
                }
            );
            return builder;
        }
    }

    extension(WebApplication app)
    {
        // Anonymous, since a Kit Host authenticates every endpoint by default. As in Aspire's template, only in
        // Development: see https://aka.ms/aspire/healthchecks before mapping them in other environments.
        public WebApplication MapDefaultEndpoints()
        {
            if (app.Environment.IsDevelopment())
            {
                app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();
                app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(LiveTag) })
                    .AllowAnonymous();
            }

            return app;
        }
    }

    // AddDbContextCheck's question, whether the database answers, for each DbContext the Host registered: a Module's
    // DbContext is internal to it, so it is found through its registered options instead of by its type.
    private sealed class DbContextsHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await using var scope = scopes.CreateAsyncScope();
            foreach (var options in scope.ServiceProvider.GetServices<DbContextOptions>())
            {
                var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
                if (!await dbContext.Database.CanConnectAsync(cancellationToken))
                {
                    return new HealthCheckResult(context.Registration.FailureStatus, $"The database of {options.ContextType.Name} does not answer.");
                }
            }

            return HealthCheckResult.Healthy();
        }
    }
}

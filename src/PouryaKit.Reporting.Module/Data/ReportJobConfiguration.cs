using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PouryaKit.Reporting.Module;

internal sealed class ReportJobConfiguration : IEntityTypeConfiguration<ReportJob>
{
    public void Configure(EntityTypeBuilder<ReportJob> builder)
    {
        builder.ToTable("report_jobs");
        builder.Property(job => job.Kind).HasMaxLength(RequestReportCommandValidator.KindLength);
        builder.Property(job => job.Format).HasConversion<string>().HasMaxLength(16);
        builder
            .Property(job => job.Parameters)
            .HasColumnType("jsonb")
            .HasConversion(
                parameters => JsonSerializer.Serialize(parameters, JsonSerializerOptions.Default),
                json =>
                    JsonSerializer.Deserialize<Dictionary<string, string>>(
                        json,
                        JsonSerializerOptions.Default
                    )!,
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
                    parameters => parameters.Count,
                    parameters => new(parameters)
                )
            );
        builder.Property(job => job.RequestedByKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(job => job.State).HasConversion<string>().HasMaxLength(16);
        // Retention reads the jobs past their expiry, and the jobs it expired, across every tenant.
        builder.HasIndex(job => new { job.State, job.ExpiresAt });
    }
}

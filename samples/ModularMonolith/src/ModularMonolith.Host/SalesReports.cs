using System.Globalization;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Reporting;
using PouryaKit.Reporting.Module;

namespace ModularMonolith.Host;

/// <summary>
/// The Host's one report kind, <see cref="Kind"/>: a month's sales, which a person of a tenant may request for a month
/// given as <c>yyyy-MM</c>, laid out as one table. A real application reads the lines from its Modules, asking each
/// through its Contracts with the context's <c>Continue()</c>; the sample's are fixed.
/// </summary>
public sealed class SalesReports : IReportRequestAuthorizer, IReportLayouts
{
    public const string Kind = "sales";

    public const string Month = "month";

    public Task<bool> MayRequestAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(
            context.Actor.Kind == ActorKind.Person && context.Tenant is not null && request.Kind == Kind && MonthOf(request) is not null
        );
    }

    public Task<Result<ReportLayout>> LayOutAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var month = MonthOf(request) ?? throw new InvalidOperationException($"The {Kind} report was requested without a month.");
        Result<ReportLayout> layout = new ReportLayout(
            "Monthly sales",
            new ReportTable(
                [new("Product"), new("Quantity", ReportAlignment.Right), new("Amount", ReportAlignment.Right)],
                [["Tea", "12", "30.00"], ["Coffee", "7", "24.50"]]
            )
        )
        {
            Introduction =
            [
                new ReportHeading(month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)),
                new ReportParagraph($"Every order settled this month, for {context.Tenant}."),
            ],
            PageFooter = "Confidential",
            Branding = new() { Accent = new(0x1F, 0x4E, 0x79) },
        };
        return Task.FromResult(layout);
    }

    // The one parameter the kind takes, exactly.
    private static DateOnly? MonthOf(ReportRequest request) =>
        request.Parameters.Count == 1
        && request.Parameters.TryGetValue(Month, out var month)
        && DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}

using PouryaKit.Domain;

namespace PouryaKit.Reporting;

/// <summary>
/// The renderer of the Reporting Capability: PDF through MigraDoc and PDFsharp, CSV through CsvHelper, spreadsheets
/// through the Open XML SDK. It keeps no state of its own, so one instance serves every caller at once.
/// </summary>
public sealed class ReportRenderer : IReportRenderer
{
    /// <inheritdoc />
    public Result<RenderedReport> Render(ReportLayout layout, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var table = layout.Table;
        if (table.Columns.Count == 0 || table.Rows.Any(row => row.Count != table.Columns.Count))
        {
            return ReportErrors.TableShape;
        }

        if (layout.Branding.Logo is { } logo && !IsPngOrJpeg(logo))
        {
            return ReportErrors.LogoFormat;
        }

        return format switch
        {
            ReportFormat.Pdf => new RenderedReport(PdfReport.Render(layout), "application/pdf", ".pdf"),
            ReportFormat.Csv => new RenderedReport(CsvReport.Render(table), "text/csv", ".csv"),
            ReportFormat.Spreadsheet => new RenderedReport(
                SpreadsheetReport.Render(table),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".xlsx"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No such report format."),
        };
    }

    private static bool IsPngOrJpeg(byte[] image) =>
        image.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
        || image.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
}

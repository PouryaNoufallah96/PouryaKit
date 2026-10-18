using PouryaKit.Domain;

namespace PouryaKit.Reporting;

/// <summary>The Errors a <see cref="IReportRenderer"/> refuses a layout with; nothing was rendered.</summary>
public static class ReportErrors
{
    /// <summary>The layout's table has no column, or a row whose number of cells differs from its number of columns.</summary>
    public static readonly Error TableShape = Error.Validation(
        "Reporting.TableShape",
        "The report's table has no column, or a row whose number of cells differs from its number of columns.",
        nameof(ReportLayout.Table)
    );

    /// <summary>
    /// The branding's logo is not a PNG or JPEG image PDFsharp can load, such as another format or a file cut short; a PDF
    /// draws those two formats only.
    /// </summary>
    public static readonly Error LogoFormat = Error.Validation(
        "Reporting.LogoFormat",
        "The report's logo is not a readable PNG or JPEG image.",
        nameof(ReportBranding.Logo)
    );
}

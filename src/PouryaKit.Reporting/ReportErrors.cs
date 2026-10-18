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

    /// <summary>The branding's logo is neither a PNG nor a JPEG image, the two formats a PDF draws.</summary>
    public static readonly Error LogoFormat = Error.Validation(
        "Reporting.LogoFormat",
        "The report's logo is neither a PNG nor a JPEG image.",
        nameof(ReportBranding.Logo)
    );
}

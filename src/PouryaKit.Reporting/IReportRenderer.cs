using PouryaKit.Domain;

namespace PouryaKit.Reporting;

/// <summary>
/// Renders a Module's <see cref="ReportLayout"/> into a file in memory; storing the file, such as through
/// <c>IFileStorage</c>, is the caller's. <see cref="ReportRenderer"/> is the implementation, which a Module registers
/// as a singleton.
/// </summary>
public interface IReportRenderer
{
    /// <summary>
    /// Renders <paramref name="layout"/> as <paramref name="format"/>. Fails with <see cref="ReportErrors.TableShape"/>
    /// or <see cref="ReportErrors.LogoFormat"/>, rendering nothing, when the layout cannot be drawn.
    /// </summary>
    Result<RenderedReport> Render(ReportLayout layout, ReportFormat format);
}

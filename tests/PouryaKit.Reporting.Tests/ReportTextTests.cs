using System.Reflection;
using System.Runtime.CompilerServices;

namespace PouryaKit.Reporting.Tests;

// A report's introduction holds headings and paragraphs only. ReportText is closed, so no other assembly can declare a
// third kind for the PDF to draw as a paragraph, and the PDF's switch over the two has no default arm.
public sealed class ReportTextTests
{
    [Fact]
    public void A_report_text_is_a_heading_or_a_paragraph_and_no_other_assembly_can_add_a_kind()
    {
        var closed = typeof(ReportText).GetCustomAttribute<IsClosedTypeAttribute>();

        Assert.NotNull(closed);
        Assert.Equal([typeof(ReportHeading), typeof(ReportParagraph)], closed.DerivedTypes);
    }
}

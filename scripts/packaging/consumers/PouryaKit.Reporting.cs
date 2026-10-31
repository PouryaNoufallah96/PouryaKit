using PouryaKit.Domain;
using PouryaKit.Reporting;

// A Module's layout through the packed renderer: one table and its introduction, rendered to each format, and a row of
// another width refused with an Error.
IReportRenderer renderer = new ReportRenderer();
var layout = new ReportLayout(
    "Monthly sales",
    new ReportTable(
        [new("Product"), new("Amount", ReportAlignment.Right)],
        [
            ["Tea", "12.50"],
        ]
    )
)
{
    Introduction =
    [
        new ReportHeading("October 2026"),
        new ReportParagraph("Every order settled this month."),
    ],
    PageFooter = "Confidential",
};
Result<RenderedReport>[] rendered =
[
    .. Enum.GetValues<ReportFormat>().Select(format => renderer.Render(layout, format)),
];
var refused = renderer.Render(
    layout with
    {
        Table = layout.Table with
        {
            Rows =
            [
                ["Tea"],
            ],
        },
    },
    ReportFormat.Csv
);
Console.WriteLine(
    rendered.All(report => report.IsSuccess) && refused.Errors.Contains(ReportErrors.TableShape)
        ? 1
        : 0
);

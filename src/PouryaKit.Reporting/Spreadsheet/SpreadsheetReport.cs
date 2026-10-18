using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PouryaKit.Reporting;

// One worksheet, the headers in its first row, every cell an inline string: a spreadsheet application shows each cell
// as the Module formatted it and evaluates none as a formula.
internal static class SpreadsheetReport
{
    public static byte[] Render(ReportTable table)
    {
        using var content = new MemoryStream();
        using (var spreadsheet = SpreadsheetDocument.Create(content, SpreadsheetDocumentType.Workbook))
        {
            var workbook = spreadsheet.AddWorkbookPart();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            var rows = new SheetData(Row(table.Columns.Select(column => column.Header)));
            rows.Append(table.Rows.Select(Row));
            worksheet.Worksheet = new Worksheet(rows);
            workbook.Workbook = new Workbook(
                new Sheets(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Report" })
            );
        }

        return content.ToArray();
    }

    private static Row Row(IEnumerable<string> cells) =>
        new(cells.Select(cell => new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(cell) { Space = SpaceProcessingModeValues.Preserve }),
        }));
}

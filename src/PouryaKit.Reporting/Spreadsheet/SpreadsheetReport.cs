using System.Buffers;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PouryaKit.Reporting;

// One worksheet, the headers in its first row, every cell an inline string: a spreadsheet application shows each cell
// as the Module formatted it and evaluates none as a formula.
internal static class SpreadsheetReport
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789ABCDEFabcdef");

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
            InlineString = new InlineString(new Text(Encoded(cell)) { Space = SpaceProcessingModeValues.Preserve }),
        }));

    // A spreadsheet application reads _xHHHH_ in a string as the character HHHH (ECMA-376 Part 1, 22.9.2.19), so a
    // literal one keeps its underscore as _x005F_, and a character XML cannot hold, such as a control character, is
    // written in that form.
    private static string Encoded(string text)
    {
        var encoded = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var rest = text.AsSpan(index);
            if (rest is ['_', 'x', _, _, _, _, '_', ..] && !rest[2..6].ContainsAnyExcept(HexDigits))
            {
                encoded.Append("_x005F");
            }

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[index + 1], character))
            {
                encoded.Append(character).Append(text[++index]);
            }
            else if (XmlConvert.IsXmlChar(character))
            {
                encoded.Append(character);
            }
            else
            {
                encoded.Append($"_x{(int)character:X4}_");
            }
        }

        return encoded.ToString();
    }
}

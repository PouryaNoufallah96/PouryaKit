using System.Buffers;
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace PouryaKit.Reporting;

// RFC 4180 with the invariant culture, in UTF-8 with a byte order mark, so a spreadsheet application opens non-ASCII
// text as written. A cell that starts with =, +, -, @, a tab or a carriage return is written with a leading apostrophe,
// so a spreadsheet application opening the file does not run it as a formula, unless the whole cell is a number in the
// invariant culture, such as -4 or +1,250.50, which stays a number. An empty field is quoted, so the empty row of a
// one-column table is not a blank line a reader skips.
internal static class CsvReport
{
    private static readonly SearchValues<char> FormulaStarts = SearchValues.Create("=+-@\t\r");

    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        ShouldQuote = args => string.IsNullOrEmpty(args.Field) || ConfigurationFunctions.ShouldQuote(args),
    };

    public static byte[] Render(ReportTable table)
    {
        using var content = new MemoryStream();
        using (var writer = new StreamWriter(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        using (var csv = new CsvWriter(writer, Configuration))
        {
            foreach (var column in table.Columns)
            {
                csv.WriteField(Escaped(column.Header));
            }

            csv.NextRecord();
            foreach (var row in table.Rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(Escaped(cell));
                }

                csv.NextRecord();
            }
        }

        return content.ToArray();
    }

    private static string Escaped(string cell) =>
        cell.Length > 0
        && FormulaStarts.Contains(cell[0])
        && !decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
            ? $"'{cell}"
            : cell;
}

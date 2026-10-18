using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace PouryaKit.Reporting;

// RFC 4180 with the invariant culture, in UTF-8 with a byte order mark, so a spreadsheet application opens non-ASCII
// text as written. A cell that starts with =, @, +, -, a tab or a carriage return is written with a leading
// apostrophe, so a spreadsheet application opening the file does not run it as a formula (CsvHelper's injection escape).
internal static class CsvReport
{
    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        InjectionOptions = InjectionOptions.Escape,
    };

    public static byte[] Render(ReportTable table)
    {
        using var content = new MemoryStream();
        using (var writer = new StreamWriter(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        using (var csv = new CsvWriter(writer, Configuration))
        {
            foreach (var column in table.Columns)
            {
                csv.WriteField(column.Header);
            }

            csv.NextRecord();
            foreach (var row in table.Rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(cell);
                }

                csv.NextRecord();
            }
        }

        return content.ToArray();
    }
}

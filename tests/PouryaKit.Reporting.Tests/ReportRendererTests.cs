using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PouryaKit.Reporting.Tests;

// One layout, as a Module writes it, rendered to each format and read back by that format's standard reader.
public sealed class ReportRendererTests
{
    // A 1×1 PNG image.
    private static readonly byte[] Logo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
    );

    // A 1×1 JPEG image.
    private static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAAAaADAAQAAAABAAAAAQAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgAAQABAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAGxsbGxsbLxsbL0IvLy9CWUJCQkJZcFlZWVlZcIhwcHBwcHCIiIiIiIiIiKOjo6Ojo76+vr6+1dXV1dXV1dXV1f/bAEMBISMjNjI2XTIyXd+XfJff39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f39/f3//dAAQAAf/aAAwDAQACEQMRAD8Akooor0CT/9k="
    );

    private static readonly IReadOnlyList<IReadOnlyList<string>> Rows =
    [
        .. Enumerable.Range(1, 80).Select(day => (IReadOnlyList<string>)[$"Product {day}", $"{day * 3}", $"{day * 12.5m:0.00}"]),
        ["=HYPERLINK(\"https://example.com\")", "-4", "  padded  "],
        ["-4+cmd", "+1,250.50", "@SUM(A1)"],
    ];

    private static readonly ReportLayout Sales = new(
        "Monthly sales",
        new ReportTable([new("Product"), new("Quantity", ReportAlignment.Right), new("Amount", ReportAlignment.Right)], Rows)
    )
    {
        Introduction = [new ReportHeading("October 2026"), new ReportParagraph("Every order the shop settled this month.")],
        PageHeader = "Acme Shop",
        PageFooter = "Confidential",
        Branding = new() { Logo = Logo, Accent = new(0x1F, 0x4E, 0x79) },
    };

    private readonly ReportRenderer _renderer = new();

    [Fact]
    public void The_pdf_opens_in_pdfsharps_reader_with_its_pages_logo_and_embedded_font()
    {
        var report = _renderer.Render(Sales, ReportFormat.Pdf).Value;

        Assert.Equal(("application/pdf", ".pdf"), (report.ContentType, report.FileExtension));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(report.Content, 0, 5));
        using var pdf = PdfReader.Open(new MemoryStream(report.Content), PdfDocumentOpenMode.Import);
        Assert.Equal(3, pdf.PageCount);
        Assert.Equal("Monthly sales", pdf.Info.Title);
        Assert.NotNull(pdf.Pages[0].Resources.Elements.GetDictionary("/XObject"));
        // Each font a page uses is embedded, so the file renders where no system font is installed.
        foreach (var page in pdf.Pages)
        {
            var fonts = page.Resources.Elements.GetDictionary("/Font")!;
            Assert.NotEmpty(fonts.Elements.Keys);
            Assert.All(fonts.Elements.Keys, key => Assert.True(Embedded(fonts.Elements.GetDictionary(key)!), $"{key} is not embedded."));
        }
    }

    [Fact]
    public void The_spreadsheet_opens_in_the_open_xml_sdk_valid_with_every_cell_as_text()
    {
        var report = _renderer.Render(Sales, ReportFormat.Spreadsheet).Value;

        Assert.Equal(".xlsx", report.FileExtension);
        using var spreadsheet = SpreadsheetDocument.Open(new MemoryStream(report.Content), isEditable: false);
        Assert.Empty(new OpenXmlValidator().Validate(spreadsheet, TestContext.Current.CancellationToken));
        var workbook = spreadsheet.WorkbookPart!;
        var sheet = Assert.Single(workbook.Workbook!.Sheets!.Elements<Sheet>());
        Assert.Equal("Report", sheet.Name?.Value);
        var worksheet = ((WorksheetPart)workbook.GetPartById(sheet.Id!.Value!)).Worksheet!;
        IReadOnlyList<IReadOnlyList<string>> expected = [["Product", "Quantity", "Amount"], .. Rows];
        Assert.Equal(expected, Cells(worksheet));
        Assert.Empty(worksheet.Descendants<CellFormula>());
    }

    [Fact]
    public void The_csv_reads_back_in_csvhelper_with_its_headers_rows_formulas_escaped_and_numbers_kept()
    {
        var report = _renderer.Render(Sales, ReportFormat.Csv).Value;

        Assert.Equal(("text/csv", ".csv"), (report.ContentType, report.FileExtension));
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), report.Content[..3]);
        IReadOnlyList<IReadOnlyList<string>> expected =
        [
            ["Product", "Quantity", "Amount"],
            .. Rows.SkipLast(2),
            ["'=HYPERLINK(\"https://example.com\")", "-4", "  padded  "],
            ["'-4+cmd", "+1,250.50", "'@SUM(A1)"],
        ];
        Assert.Equal(expected, Records(report));
    }

    [Fact]
    public void The_empty_rows_of_a_one_column_csv_read_back()
    {
        var notes = new ReportLayout("Notes", new ReportTable([new("Note")], [[""], ["a"], [""], ["b"], [""]]));

        IReadOnlyList<IReadOnlyList<string>> expected = [["Note"], [""], ["a"], [""], ["b"], [""]];
        Assert.Equal(expected, Records(_renderer.Render(notes, ReportFormat.Csv).Value));
    }

    [Fact]
    public void The_spreadsheet_encodes_escape_like_text_and_control_characters_so_they_read_back_verbatim()
    {
        IReadOnlyList<IReadOnlyList<string>> rows = [["Site_x0020_Column"], ["a\u0001b"], ["_x12_ and _xZZZZ_"], ["\U0001F600"]];
        var codes = new ReportLayout("Codes", new ReportTable([new("Code_x0041_")], rows));

        var report = _renderer.Render(codes, ReportFormat.Spreadsheet).Value;

        using var spreadsheet = SpreadsheetDocument.Open(new MemoryStream(report.Content), isEditable: false);
        Assert.Empty(new OpenXmlValidator().Validate(spreadsheet, TestContext.Current.CancellationToken));
        var cells = Cells(spreadsheet.WorkbookPart!.WorksheetParts.Single().Worksheet!);
        Assert.Equal("Site_x005F_x0020_Column", cells[1][0]);
        // A spreadsheet application reads _xHHHH_ as the character HHHH (ECMA-376 Part 1, 22.9.2.19).
        IReadOnlyList<IReadOnlyList<string>> expected = [["Code_x0041_"], .. rows];
        Assert.Equal(
            expected,
            [.. cells.Select(row => (IReadOnlyList<string>)[.. row.Select(cell => Regex.Replace(cell, "_x([0-9A-Fa-f]{4})_", hex => ((char)Convert.ToInt32(hex.Groups[1].Value, 16)).ToString()))])]
        );
    }

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Csv)]
    [InlineData(ReportFormat.Spreadsheet)]
    public void A_table_without_columns_or_with_a_row_of_another_width_renders_nothing(ReportFormat format)
    {
        var shortRow = Sales with { Table = Sales.Table with { Rows = [.. Rows, ["Only a product"]] } };
        var wideRow = Sales with { Table = Sales.Table with { Rows = [.. Rows, ["Tea", "1", "2.00", "extra"]] } };
        var noColumns = Sales with { Table = new ReportTable([], []) };

        Assert.Equal([ReportErrors.TableShape], _renderer.Render(shortRow, format).Errors);
        Assert.Equal([ReportErrors.TableShape], _renderer.Render(wideRow, format).Errors);
        Assert.Equal([ReportErrors.TableShape], _renderer.Render(noColumns, format).Errors);
    }

    [Fact]
    public void A_logo_that_is_not_a_readable_png_or_jpeg_renders_nothing()
    {
        // A GIF, a PNG cut inside its header and a JPEG cut before its frame header: MigraDoc would draw a placeholder.
        byte[][] unreadable = [Encoding.ASCII.GetBytes("GIF89a"), Logo[..(Logo.Length / 2)], Jpeg[..40]];
        foreach (var logo in unreadable)
        {
            Assert.Equal([ReportErrors.LogoFormat], _renderer.Render(Sales with { Branding = new() { Logo = logo } }, ReportFormat.Pdf).Errors);
        }

        Assert.True(_renderer.Render(Sales with { Branding = new() { Logo = Jpeg } }, ReportFormat.Pdf).IsSuccess);
        // A CSV and a spreadsheet hold the table alone, so the same logo stops neither.
        Assert.True(_renderer.Render(Sales with { Branding = new() { Logo = unreadable[0] } }, ReportFormat.Csv).IsSuccess);
        Assert.True(_renderer.Render(Sales with { Branding = new() { Logo = unreadable[0] } }, ReportFormat.Spreadsheet).IsSuccess);
    }

    [Fact]
    public async Task One_renderer_renders_pdfs_for_concurrent_callers()
    {
        var renders = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => _renderer.Render(Sales, ReportFormat.Pdf), TestContext.Current.CancellationToken))
        );

        Assert.All(renders, render => Assert.Equal(3, PdfReader.Open(new MemoryStream(render.Value.Content), PdfDocumentOpenMode.Import).PageCount));
    }

    private static List<IReadOnlyList<string>> Records(RenderedReport report)
    {
        using var csv = new CsvReader(new StreamReader(new MemoryStream(report.Content)), CultureInfo.InvariantCulture);
        var records = new List<IReadOnlyList<string>>();
        while (csv.Read())
        {
            records.Add([.. Enumerable.Range(0, csv.Parser.Count).Select(index => csv.GetField(index)!)]);
        }

        return records;
    }

    private static List<IReadOnlyList<string>> Cells(Worksheet worksheet) =>
        [.. worksheet.Descendants<Row>().Select(row => (IReadOnlyList<string>)[.. row.Elements<Cell>().Select(cell => cell.InlineString!.InnerText)])];

    // A TrueType font is embedded when its descriptor, or that of the CID font a composite font descends to, holds the
    // font program.
    private static bool Embedded(PdfDictionary font) =>
        font.Elements.GetArray("/DescendantFonts") is { Elements.Count: > 0 } descendants
            ? Embedded(descendants.Elements.GetDictionary(0)!)
            : font.Elements.GetDictionary("/FontDescriptor")?.Elements.ContainsKey("/FontFile2") == true;
}

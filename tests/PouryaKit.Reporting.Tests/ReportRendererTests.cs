using System.Globalization;
using System.Text;
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

    private static readonly IReadOnlyList<IReadOnlyList<string>> Rows =
    [
        .. Enumerable.Range(1, 80).Select(day => (IReadOnlyList<string>)[$"Product {day}", $"{day * 3}", $"{day * 12.5m:0.00}"]),
        ["=HYPERLINK(\"https://example.com\")", "-4", "  padded  "],
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
    public void The_csv_reads_back_in_csvhelper_with_its_headers_rows_and_formulas_escaped()
    {
        var report = _renderer.Render(Sales, ReportFormat.Csv).Value;

        Assert.Equal(("text/csv", ".csv"), (report.ContentType, report.FileExtension));
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), report.Content[..3]);
        using var csv = new CsvReader(new StreamReader(new MemoryStream(report.Content)), CultureInfo.InvariantCulture);
        var records = new List<IReadOnlyList<string>>();
        while (csv.Read())
        {
            records.Add([.. Enumerable.Range(0, csv.Parser.Count).Select(index => csv.GetField(index)!)]);
        }

        IReadOnlyList<IReadOnlyList<string>> expected =
        [
            ["Product", "Quantity", "Amount"],
            .. Rows.SkipLast(1),
            ["'=HYPERLINK(\"https://example.com\")", "'-4", "  padded  "],
        ];
        Assert.Equal(expected, records);
    }

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Csv)]
    [InlineData(ReportFormat.Spreadsheet)]
    public void A_table_without_columns_or_with_a_row_of_another_width_renders_nothing(ReportFormat format)
    {
        var shortRow = Sales with { Table = Sales.Table with { Rows = [.. Rows, ["Only a product"]] } };
        var noColumns = Sales with { Table = new ReportTable([], []) };

        Assert.Equal([ReportErrors.TableShape], _renderer.Render(shortRow, format).Errors);
        Assert.Equal([ReportErrors.TableShape], _renderer.Render(noColumns, format).Errors);
    }

    [Fact]
    public void A_logo_that_is_neither_png_nor_jpeg_renders_nothing()
    {
        var gif = Sales with { Branding = Sales.Branding with { Logo = Encoding.ASCII.GetBytes("GIF89a") } };

        Assert.Equal([ReportErrors.LogoFormat], _renderer.Render(gif, ReportFormat.Pdf).Errors);
        Assert.True(_renderer.Render(Sales with { Branding = new() { Logo = [0xFF, 0xD8, 0xFF, 0xE0] } }, ReportFormat.Csv).IsSuccess);
    }

    [Fact]
    public async Task One_renderer_renders_pdfs_for_concurrent_callers()
    {
        var renders = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => _renderer.Render(Sales, ReportFormat.Pdf), TestContext.Current.CancellationToken))
        );

        Assert.All(renders, render => Assert.Equal(3, PdfReader.Open(new MemoryStream(render.Value.Content), PdfDocumentOpenMode.Import).PageCount));
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

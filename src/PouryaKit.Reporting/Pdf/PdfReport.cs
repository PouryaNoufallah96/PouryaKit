using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace PouryaKit.Reporting;

// An A4 portrait document: the logo, the title, the introduction and the table, whose header row repeats on every
// page, with the page header and footer on every page.
internal static class PdfReport
{
    private const double MarginCentimetres = 2;
    private const double ContentCentimetres = 21 - 2 * MarginCentimetres;

    public static byte[] Render(ReportLayout layout)
    {
        RobotoFontResolver.Install();

        var document = new Document();
        document.Info.Title = layout.Title;
        var accent = new Color(layout.Branding.Accent.Red, layout.Branding.Accent.Green, layout.Branding.Accent.Blue);
        document.Styles[StyleNames.Normal]!.Font.Name = RobotoFontResolver.Family;
        document.Styles[StyleNames.Normal]!.Font.Size = 10;
        document.Styles[StyleNames.Heading1]!.Font.Size = 18;
        document.Styles[StyleNames.Heading1]!.Font.Bold = true;
        document.Styles[StyleNames.Heading1]!.Font.Color = accent;
        document.Styles[StyleNames.Heading1]!.ParagraphFormat.SpaceAfter = Unit.FromPoint(8);
        document.Styles[StyleNames.Heading2]!.Font.Size = 13;
        document.Styles[StyleNames.Heading2]!.Font.Bold = true;
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.SpaceBefore = Unit.FromPoint(8);
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Portrait;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(MarginCentimetres);
        section.PageSetup.RightMargin = Unit.FromCentimeter(MarginCentimetres);

        if (layout.PageHeader is { } header)
        {
            section.Headers.Primary.AddParagraph(header);
        }

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        if (layout.PageFooter is { } footerText)
        {
            footer.AddText($"{footerText} · ");
        }

        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();

        if (layout.Branding.Logo is { } logo)
        {
            var image = section.AddImage($"base64:{Convert.ToBase64String(logo)}");
            image.Width = Unit.FromCentimeter(4);
            image.LockAspectRatio = true;
        }

        section.AddParagraph(layout.Title, StyleNames.Heading1);
        foreach (var text in layout.Introduction)
        {
            section.AddParagraph(text.Text, text is ReportHeading ? StyleNames.Heading2 : StyleNames.Normal);
        }

        section.LastParagraph?.Format.SpaceAfter = Unit.FromPoint(8);

        AddTable(section, layout.Table, accent);

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var pdf = new MemoryStream();
        renderer.PdfDocument.Save(pdf, closeStream: false);
        return pdf.ToArray();
    }

    private static void AddTable(Section section, ReportTable report, Color accent)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.LightGray;
        table.TopPadding = Unit.FromPoint(2);
        table.BottomPadding = Unit.FromPoint(2);
        table.Rows.VerticalAlignment = VerticalAlignment.Center;
        foreach (var column in report.Columns)
        {
            table.AddColumn(Unit.FromCentimeter(ContentCentimetres / report.Columns.Count)).Format.Alignment = column.Alignment switch
            {
                ReportAlignment.Center => ParagraphAlignment.Center,
                ReportAlignment.Right => ParagraphAlignment.Right,
                _ => ParagraphAlignment.Left,
            };
        }

        var headerRow = table.AddRow();
        headerRow.HeadingFormat = true;
        headerRow.Shading.Color = accent;
        headerRow.Format.Font.Bold = true;
        headerRow.Format.Font.Color = Colors.White;
        for (var index = 0; index < report.Columns.Count; index++)
        {
            headerRow.Cells[index].AddParagraph(report.Columns[index].Header);
        }

        foreach (var cells in report.Rows)
        {
            var row = table.AddRow();
            for (var index = 0; index < cells.Count; index++)
            {
                row.Cells[index].AddParagraph(cells[index]);
            }
        }
    }
}

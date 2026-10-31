namespace PouryaKit.Reporting;

/// <summary>
/// A report a Module lays out once and renders to any <see cref="ReportFormat"/>: a titled document with an
/// introduction and one table. A PDF draws all of it; a CSV file and a spreadsheet hold the table alone.
/// </summary>
/// <param name="Title">The report's title, drawn at the top of a PDF's first page in the branding's accent colour.</param>
/// <param name="Table">The report's one table.</param>
public sealed record ReportLayout(string Title, ReportTable Table)
{
    /// <summary>The headings and paragraphs a PDF draws, in order, between the title and the table.</summary>
    public IReadOnlyList<ReportText> Introduction { get; init; } = [];

    /// <summary>Text a PDF draws at the top of every page; none when null.</summary>
    public string? PageHeader { get; init; }

    /// <summary>Text a PDF draws at the foot of every page, before the page number; the page number alone when null.</summary>
    public string? PageFooter { get; init; }

    /// <summary>The logo and accent colour a PDF draws the report with.</summary>
    public ReportBranding Branding { get; init; } = new();
}

/// <summary>A heading or a paragraph of a report's introduction.</summary>
/// <param name="Text">The text, drawn as given.</param>
public abstract record ReportText(string Text);

/// <summary>A heading of a report's introduction.</summary>
/// <param name="Text">The heading's text.</param>
public sealed record ReportHeading(string Text) : ReportText(Text);

/// <summary>A paragraph of a report's introduction.</summary>
/// <param name="Text">The paragraph's text.</param>
public sealed record ReportParagraph(string Text) : ReportText(Text);

/// <summary>
/// A report's table: its columns and its rows of text cells, one cell per column. A Module formats each value, such as
/// a number or a date, as the text the report shows.
/// </summary>
/// <param name="Columns">The columns, at least one, in order.</param>
/// <param name="Rows">The rows, each with exactly one cell per column.</param>
public sealed record ReportTable(
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows
);

/// <summary>A column of a report's table.</summary>
/// <param name="Header">The column's header, the first row of a CSV file and a spreadsheet.</param>
/// <param name="Alignment">How a PDF aligns the column's cells, such as <see cref="ReportAlignment.Right"/> for amounts.</param>
public sealed record ReportColumn(string Header, ReportAlignment Alignment = ReportAlignment.Left);

/// <summary>How a PDF aligns the cells of a column.</summary>
public enum ReportAlignment
{
    /// <summary>Aligned to the cell's left edge.</summary>
    Left,

    /// <summary>Centred in the cell.</summary>
    Center,

    /// <summary>Aligned to the cell's right edge.</summary>
    Right,
}

/// <summary>
/// The branding a PDF draws a report with: a logo above the title, and an accent colour for the title and the table's
/// header row. The text is set in the Roboto font the package embeds in every PDF.
/// </summary>
public sealed record ReportBranding
{
    /// <summary>The logo, a PNG or JPEG image, drawn at the top of the first page; none when null.</summary>
    public byte[]? Logo { get; init; }

    /// <summary>The colour of the title and of the table's header row, dark grey unless set.</summary>
    public ReportColor Accent { get; init; } = new(0x33, 0x33, 0x33);
}

/// <summary>An opaque colour of a report's branding.</summary>
/// <param name="Red">Its red component.</param>
/// <param name="Green">Its green component.</param>
/// <param name="Blue">Its blue component.</param>
public readonly record struct ReportColor(byte Red, byte Green, byte Blue);

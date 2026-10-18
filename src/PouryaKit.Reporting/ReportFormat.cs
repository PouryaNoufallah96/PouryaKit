namespace PouryaKit.Reporting;

/// <summary>A file format a <see cref="ReportLayout"/> renders to.</summary>
public enum ReportFormat
{
    /// <summary>A PDF document with the whole layout, its font embedded.</summary>
    Pdf,

    /// <summary>A UTF-8 CSV file of the layout's table, its headers first.</summary>
    Csv,

    /// <summary>An Office Open XML spreadsheet (<c>.xlsx</c>) with the layout's table on one worksheet, its headers first.</summary>
    Spreadsheet,
}

/// <summary>A rendered report: the file's bytes, with the content type and file extension to store it under.</summary>
/// <param name="Content">The file's bytes.</param>
/// <param name="ContentType">The file's media type, such as <c>application/pdf</c>, for <c>IFileStorage.UploadAsync</c>.</param>
/// <param name="FileExtension">The file's extension with its dot, such as <c>.pdf</c>.</param>
public sealed record RenderedReport(byte[] Content, string ContentType, string FileExtension);

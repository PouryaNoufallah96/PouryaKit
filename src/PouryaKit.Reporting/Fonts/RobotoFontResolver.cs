using PdfSharp.Fonts;

namespace PouryaKit.Reporting;

// PDFsharp's Core build resolves no system font on Linux or macOS, so every family a document names resolves to the
// Roboto faces this assembly embeds, and the PDF carries them. PDFsharp holds one resolver per process, which it lets
// no one change once a font was used: Install sets this one unless the Host set its own first, which then resolves
// the report's fonts.
internal sealed class RobotoFontResolver : IFontResolver
{
    public const string Family = "Roboto";

    private const string Regular = "PouryaKit.Reporting.Roboto-Regular.ttf";
    private const string Bold = "PouryaKit.Reporting.Roboto-Bold.ttf";

    private static readonly RobotoFontResolver Instance = new();

    public static void Install() => GlobalFontSettings.FontResolver ??= Instance;

    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: italic);

    public byte[] GetFont(string faceName)
    {
        using var font =
            typeof(RobotoFontResolver).Assembly.GetManifestResourceStream(faceName)
            ?? throw new InvalidOperationException(
                $"PouryaKit.Reporting embeds no font face {faceName}."
            );
        using var bytes = new MemoryStream();
        font.CopyTo(bytes);
        return bytes.ToArray();
    }
}

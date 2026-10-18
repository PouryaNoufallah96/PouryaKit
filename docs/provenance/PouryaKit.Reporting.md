# PouryaKit.Reporting provenance

Adopted implementations in `PouryaKit.Reporting`. Format and rules: [README](README.md).

The renderer's code is new to the Kit: plan I.3's Reporting row rejected every reference project's renderer (Ultrabusiness renders with QuestPDF under `LicenseType.Community`, which ADR 0002 does not allow), so `src/PouryaKit.Reporting/ReportRenderer.cs`, its layout model and the three format writers under `src/PouryaKit.Reporting/Pdf/`, `Csv/` and `Spreadsheet/` have no record. The font files it embeds are the one adopted content, recorded below.

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #129); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002). The package references `PouryaKit.Domain` alone among the Kit's packages, for `Result` and `Error`.

- `PDFsharp-MigraDoc` 6.2.4, `MIT`, with a `net10.0` asset. It brings `PDFsharp` 6.2.4 (`MIT`) and, through it, `Microsoft.Extensions.Logging.Abstractions` 8.0.3 and `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2 (`MIT`). **6.2.4 is the last MIT release:** `PDFsharp` and `PDFsharp-MigraDoc` 6.5.0, the latest stable on 2026-10-18, and the 7.0.0 previews declare `AGPL-3.0-only`, which `LicenceTests` refuses (shown on #129 by restoring 6.5.0: the test named both packages). Do not bump either package past 6.2.4 without a licence decision by the owner; spec #115 verified 6.2.4.
- `DocumentFormat.OpenXml` 3.5.1, `MIT`, the latest stable. It brings `DocumentFormat.OpenXml.Framework` 3.5.1 and `System.IO.Packaging` 10.0.2, both `MIT`.
- `CsvHelper` 33.1.0, `MS-PL OR Apache-2.0`, taken under `Apache-2.0`, the latest stable, with no dependency on `net10.0`.
- `System.Security.Cryptography.Pkcs` 10.0.13, `MIT`: a direct reference that raises the 8.0.1 `PDFsharp` 6.2.4 asks for to the first release outside CVE-2026-84008 (GHSA-846f-f764-76mx, high) and CVE-2026-70162 (GHSA-2fmv-rv2c-59x7, moderate); without it NuGet's audit fails the build (NU1902, NU1903). The floor `PouryaKit.Email.MailKit` takes for MimeKit (#124), outside plan section C's standing rule, taken by the session for #129 on 2026-10-18 for the owner to confirm. Delete it once a PDFsharp release with an MIT licence asks for 10.0.13 or later; since PDFsharp's later releases are AGPL, expect it to stay.

QuestPDF and ClosedXML fail the licence test and are not selected (spec #115, E1); `PackageGraph` keeps them, a storage SDK and `PouryaKit.Storage` out of the package's closure. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Roboto font

- **Kit files:** `src/PouryaKit.Reporting/Fonts/Roboto-Regular.ttf`, `src/PouryaKit.Reporting/Fonts/Roboto-Bold.ttf`, `src/PouryaKit.Reporting/Fonts/LICENSE`
- **Upstream:** https://github.com/googlefonts/roboto-2
- **Commit:** ab4876f650a4aeb9b661c83fee47f908c739bb70
- **Upstream paths:** `Roboto-Regular.ttf` (SHA-256 `f3edb8058e523f5612bfd99d0745e661568ad85e1b6217bc62f786fabae624c6`), `Roboto-Bold.ttf` (SHA-256 `2ca2b3bfc2c2d43fa8f2b7227982d735fd537ecf113a4c56cc2d292bcc3106c8`) and `LICENSE`, from the `roboto-unhinted.zip` asset of release `v2.138`, the tag at that commit
- **Licence:** Apache-2.0
- **Adoption:** copied
- **Ticket:** #129
- **Intentional differences:**
  - Two of the release's faces only, regular and bold; `src/PouryaKit.Reporting/Fonts/RobotoFontResolver.cs` resolves every family a PDF names to them and has PDFsharp simulate an italic. The files are unchanged, embedded as resources of the assembly, and the package ships the licence at `fonts/Roboto-LICENSE`.
  - Roboto 2.x, the last line under Apache-2.0: Roboto 3 is under the SIL Open Font License, which is not on the Kit's permissive list. PDFsharp's own Segoe WP faces (`PdfSharp.WPFonts`) are not used: PDFsharp's documentation offers them as a demonstration, not for production, and states no licence for them.

---
name: pdf-and-file-rendering-facts
description: PouryaKit.Reporting (#129) - PDFsharp's AGPL switch, its set-once font resolver and Linux fonts, MigraDoc and Open XML traps, CsvHelper injection, NuGet PackagePath for an extensionless file, the execution-core top row
metadata:
  type: project
---

## Reporting renderer (#129, 2026-10-18; PDFsharp-MigraDoc 6.2.4, DocumentFormat.OpenXml 3.5.1, CsvHelper 33.1.0)

- **PDFsharp and PDFsharp-MigraDoc 6.5.0 and the 7.0 previews are `AGPL-3.0-only`**; 6.2.4 is the last MIT release. A brief's "take the current stable" must be checked against the nuspec's `<license>`: restoring 6.5.0 turned `LicenceTests` red, which served as the licence red run.
- PDFsharp 6.2.4 asks for `System.Security.Cryptography.Pkcs` 8.0.1 (NU1902/NU1903 under TreatWarningsAsErrors): the #124 floor (10.0.13 direct reference) applies.
- **Fonts:** the Core build resolves no system font on macOS or Linux (it throws), so removing the resolver is a real red on a Mac. `GlobalFontSettings.FontResolver` is set-once (`SetFontResolver` ignores the same instance or type, throws once a font was used): `GlobalFontSettings.FontResolver ??= StaticInstance` is race-safe. `PdfSharp.WPFonts` (Segoe WP) is demo-only with no stated licence; Roboto 2.138 (`googlefonts/roboto-2`, Apache-2.0; Roboto 3 is OFL) was bundled. Fetch release assets through `api.github.com/repositories/<id>/releases/assets/<id>` with `Accept: application/octet-stream` (the guard refuses github.com URLs).
- **MigraDoc:** `table.Format.SpaceBefore` applies to every cell's paragraph (tall rows); `section.AddImage("base64:...")` works, and an unreadable image renders a placeholder silently, so validate the PNG/JPEG signature first. `PdfDocumentRenderer { Document = d }; RenderDocument(); PdfDocument.Save(stream, closeStream: false)`. Check a PDF's look with `sips -s format png file.pdf --out page.png` (page 1 only) and the Read tool.
- **Embedded-font assertion:** each page's `/Resources /Font` entry; a Type0 font's `/DescendantFonts[0]` `/FontDescriptor` holds `/FontFile2` (PDFsharp `Elements.GetArray`, `GetDictionary(0)`).
- **Open XML:** inline-string cells need `Text { Space = Preserve }` for padded text; `OpenXmlValidator.Validate(doc, ct)` (xUnit1051 wants the token). Switching `CellValues.InlineString` to `String` stays valid and readable, so it is no red.
- **CsvHelper:** `InjectionOptions.Escape` prefixes `=`, `@`, `+`, `-`, tab, CR with `'`, negative numbers included.
- **NuGet `PackagePath` for a file without an extension becomes a folder** (`fonts/X/LICENSE`), even with `.txt` in the path: give the source file the extension the path ends with (`LICENSE.txt`), then `unzip -l` the nupkg.
- **execution-core had no free slot:** shifting every component's `pos` y by 130 opened a top row; the page still passed every gate (re-serialize with `json.dumps(indent=2, ensure_ascii=False) + "\n"`, which differs from the source in one indent line only). Parallel tickets adding nodes must apply the shift.
- The Write tool refuses a file named like a report (`reporting.md`, `report*.md`) as a "report file", even a skill reference: write it from a scratchpad Python script.

**Why:** each cost a probe or a rerun in #129.
**How to apply:** for the report Module (#140), any PDF, spreadsheet or CSV code, any package that bundles a binary asset or licence file, and any new node on a full diagram. Related: [[email-and-provider-package-facts]], [[docs-verification-tools]], [[worktree-shell-and-check-gotchas]].

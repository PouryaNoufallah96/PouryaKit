![PouryaKit.Reporting](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Reporting.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Reporting?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Reporting)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Reporting

A Module's report laid out once and rendered in memory to a PDF, a CSV file or a spreadsheet. A Module builds a `ReportLayout` from what it read: a title, an introduction, one table of text cells and its branding. `IReportRenderer` turns it into a file and returns a Result with its bytes, content type and extension.

## Install

```bash
dotnet add package PouryaKit.Reporting --prerelease
```

## When to use it

- A Module produces a document for people to download, print or open in a spreadsheet.
- You brand a report with a logo and an accent colour.
- You need to know which layouts the renderer refuses.

It stores nothing: store the bytes through [`PouryaKit.Storage`](https://www.nuget.org/packages/PouryaKit.Storage), or let [`PouryaKit.Reporting.Module`](https://www.nuget.org/packages/PouryaKit.Reporting.Module) run the whole job in the background.

## Quick start

Register the renderer in each Module that renders reports ([Registering](https://pouryakit.vercel.app/docs/capabilities/reporting#registering)). Then build the layout and render it, as the Kit's tests do:

```csharp
private static readonly ReportLayout Sales = new(
    "Monthly sales",
    new ReportTable(
        [
            new("Product"),
            new("Quantity", ReportAlignment.Right),
            new("Amount", ReportAlignment.Right),
        ],
        Rows
    )
)
{
    Introduction =
    [
        new ReportHeading("October 2026"),
        new ReportParagraph("Every order the shop settled this month."),
    ],
    PageHeader = "Acme Shop",
    PageFooter = "Confidential",
    Branding = new() { Logo = Logo, Accent = new(0x1F, 0x4E, 0x79) },
};

var report = _renderer.Render(Sales, ReportFormat.Pdf).Value;
```

## What you get

- One layout for all three formats.
- The file in memory, as a Result.
- A logo, an accent colour and an embedded font, in the formats that show them.
- A layout the renderer cannot honour refused as an Error.

> [!WARNING]
> The Module formats every value: every cell is text, so a number, an amount or a date is formatted by the Module, in the culture it chooses. The embedded font holds no Persian or Arabic glyphs, and the PDF lays out no right-to-left text.

## Learn more

- [Reporting](https://pouryakit.vercel.app/docs/capabilities/reporting)
- [The three formats](https://pouryakit.vercel.app/docs/capabilities/reporting#the-three-formats)
- [Rendering and storing](https://pouryakit.vercel.app/docs/capabilities/reporting#rendering-and-storing)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/reporting#limits)

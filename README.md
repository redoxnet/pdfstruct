# PdfStruct

**PDF layout intelligence for .NET** — structured extraction with bounding boxes, reading order, and semantic element detection.

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)]()

## Why PdfStruct?

The .NET ecosystem has **zero** RAG-optimized PDF extraction libraries. Python has Docling, pymupdf4llm, Marker, and a handful of others — C# has nothing.

PdfStruct fills that gap: a pure .NET library that extracts **structured content** from PDFs — headings, paragraphs, tables, lists — with bounding boxes for every element. Output as Markdown (for LLM context) or JSON (for citations). No GPU, no cloud, no JVM.

## Status

PdfStruct is an early alpha. The current implementation focuses on text layout, reading order, heading detection, Markdown rendering, JSON rendering, and safety filtering. The public API and output schema may still change before the first stable release.

The library currently targets:

- `net8.0`

The development SDK is pinned by [`global.json`](global.json). `net8.0` is the baseline for new consumers; older target frameworks will be considered only if there is a concrete compatibility need.

## Quick Start

Once the package is published:

```bash
dotnet add package PdfStruct --prerelease
```

```csharp
using PdfStruct;

var parser = new PdfStructParser(new PdfStructOptions
{
    Format = OutputFormat.Both,
    SanitizeText = true
});

var result = parser.Parse("document.pdf");

// Markdown — feed directly into your RAG chunking pipeline
Console.WriteLine(result.Markdown);

// JSON — structured element tree with bounding boxes
File.WriteAllText("output.json", result.Json);
```

## Features

| Feature | Status |
|---------|--------|
| XY-Cut++ reading order (multi-column) | ✅ |
| Probabilistic heading detection (font + standalone signals) | ✅ |
| Heading-level assignment by typographic-style clustering | ✅ |
| Paragraph grouping with line-continuation merge | ✅ |
| Letter-spaced display title recovery (`MY DEAREST` → one heading) | ✅ |
| Rotated-text word grouping (e.g. arXiv left-margin watermarks) | ✅ |
| Ordered list detection with nested paragraph children | ✅ |
| Running header / footer / side-furniture filtering | ✅ |
| Bounding box per element | ✅ |
| Markdown output | ✅ |
| JSON output (structured element tree, ISO 8601 dates) | ✅ |
| Prompt injection filtering | ✅ |
| Invalid character replacement | ✅ |
| Sensitive text sanitization (optional) | ✅ |
| Pluggable regex-based heading patterns (per-corpus customization) | ✅ |
| Table extraction (bordered) | 🔜 Phase 2 |
| Unordered list detection | 🔜 Phase 2 |
| Image extraction | 🔜 Phase 2 |
| Tagged PDF structure tree | 🔜 Phase 3 |
| Inline emphasis (bold / italic runs preserved in paragraphs) | 🔜 Phase 3 |

## What works, what doesn't

PdfStruct's heading detection is **typography-driven**. Blocks are scored on font-size rarity, font-weight rarity, standalone-row layout, and short-single-line shape, and the document's distinct heading styles are clustered into a 1..N hierarchy.

Where this works well:

- **Academic papers** (`src/PdfStruct.Tests/Fixtures/plos_*.pdf`) — bold sub-headings + larger title font give clean separations across H1/H2/H3.
- **Display-typeset documents** (`src/PdfStruct.Tests/Fixtures/letter.pdf`, `src/PdfStruct.Tests/Fixtures/lorem_ipsum.pdf`) — large title, small body, no ambiguity.
- **Documents with explicit typographic hierarchy** — the U.S. Constitution's Article numbers at 24pt vs section labels at 20pt get distinct levels automatically.

Where it doesn't (yet):

- **Documents whose section markers carry no typographic distinction** — the Korean constitution's `제1장`, `제1절`, `제1관` are typeset in the same font and size as body paragraphs. Without language-specific patterns, only the document title is detected as a heading. Inject patterns via [`RegexHeadingClassifier`](src/PdfStruct/Analysis/RegexHeadingClassifier.cs) when the corpus needs it (see "Custom heading patterns" below).
- **Magazine pull-quotes** — large display type that visually quotes body text scores high on font rarity and is sometimes misclassified as a heading. Layout-level disambiguation (pull-quote shape, position offset) is not yet implemented.
- **Tables of contents with prominent page numbers** — the page-number column at heading-sized type is misclassified.
- **Inline bold or italic runs inside paragraphs** are not preserved — paragraphs are flattened to plain text on the way to Markdown and JSON. Per-line bold and italic flags are tracked internally (sourced from PdfPig's `FontDetails`) but do not yet propagate into paragraph-internal styling.
- **Tables, unordered lists, and inline images** are not yet detected (Phase 2 roadmap). Ordered numeric lists (`1. … 2. … 3. …`) are detected and emitted as `list` elements with paragraph children.

## Output

### Markdown

```markdown
# Introduction

This paper presents a novel approach to...

## Related Work

Previous studies have shown that...
```

### JSON

```json
{
  "file name": "paper.pdf",
  "number of pages": 12,
  "kids": [
    {
      "type": "heading",
      "id": 1,
      "page number": 1,
      "bounding box": [72.0, 700.0, 540.0, 730.0],
      "heading level": 1,
      "content": "Introduction"
    }
  ]
}
```

## Architecture

```
PdfStruct
├── Models/          # Content element types (heading, paragraph, table, list, ...)
├── Analysis/        # LetterGrouper, XY-Cut++ layout analyzer, font + regex
│                    #   classifiers, list detector, document statistics
├── Rendering/       # Markdown & JSON renderers
├── Safety/          # Prompt injection filtering, text sanitization
├── PdfStructParser  # Main entry point
└── PdfStructOptions # Configuration
```

Built on [PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0) for low-level PDF access.

## CLI

The repo includes a small local console app for trying PdfStruct against real PDFs. Drop your input documents into [`playground/`](playground/) (gitignored) and run:

```bash
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf -o out.md
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf -o out.json --format json
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf --sanitize -o out.md
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf --debug-image out/debug
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf --debug-image out/debug --debug-lines
dotnet run --project src/PdfStruct.Cli -- extract playground/document.pdf --include-running-headers -o out.md
```

By default, detected running headers, footers, page numbers, and narrow side furniture are excluded from the main content stream. `--include-running-headers` keeps that detected page furniture. `--sanitize` masks common sensitive values (emails, phone numbers, etc.) in the extracted text. `--debug-image` writes one PNG per page rasterized through PDFium (via Docnet.Core) with extracted element bounding boxes overlaid — the page renders exactly as a viewer would display it (fonts, embedded images, vector graphics) so bbox positions are visually verifiable against the actual layout. Add `--debug-lines` with `--debug-image` to include the pre-paragraph text-line boxes used before block merging.

A `diagnose` subcommand emits a per-block CSV with the heading-probability breakdown (base, font-size rarity, font-weight rarity, bulleted boost, total) — useful for calibrating the threshold against new fixtures:

```bash
dotnet run --project src/PdfStruct.Cli -- diagnose playground/document.pdf -o scores.csv
```

## Development

### Prerequisites

- .NET SDK `8.0.416` or a compatible feature-band roll-forward, as defined in [`global.json`](global.json)
- Windows, Linux, or macOS

### Build and test

```bash
dotnet restore PdfStruct.sln
dotnet build PdfStruct.sln -c Release --no-restore
dotnet test PdfStruct.sln -c Release --no-build
dotnet format PdfStruct.sln --verify-no-changes --no-restore
```

### Repository conventions

- C# language version is pinned to `12.0` in [`Directory.Build.props`](Directory.Build.props).
- Nullable reference types and implicit usings are enabled repo-wide.
- Package versions are centrally managed in [`Directory.Packages.props`](Directory.Packages.props).
- NuGet restore is scoped to `nuget.org` through [`NuGet.config`](NuGet.config).
- Text files use UTF-8 and LF line endings, enforced by [`.editorconfig`](.editorconfig) and [`.gitattributes`](.gitattributes).

## Roadmap

- **Phase 1** (current): Reading order, heading/paragraph classification, running header/footer filtering, Markdown/JSON output
- **Phase 2**: Table detection, list detection, image extraction, layout-strategy auto-selection (single-column content-stream order vs. XY-Cut), pull-quote disambiguation
- **Phase 3**: Tagged PDF support, borderless table detection, inline emphasis runs

## License

[Apache License 2.0](LICENSE), with one exception: the heading-probability
signal-weight constants in
[`src/PdfStruct/Analysis/FontBasedElementClassifier.cs`](src/PdfStruct/Analysis/FontBasedElementClassifier.cs)
are derived from
[veraPDF wcag algorithms](https://github.com/veraPDF/verapdf-wcag-algorithms),
which is dual-licensed under GPLv3+ / MPLv2+. Those portions are taken
under the Mozilla Public License 2.0, so that file is itself made
available under MPL-2.0 in addition to Apache-2.0. Downstream consumers
may use that file under either licence. See [`NOTICE`](NOTICE) for full
attribution.

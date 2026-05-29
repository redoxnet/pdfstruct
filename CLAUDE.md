# CLAUDE.md

Guidance for Claude Code agents working in this repository.

## What this repo is

PdfStruct is a .NET library and CLI for **RAG-optimized structured extraction** from PDFs. It produces Markdown (for LLM context) and structured JSON with per-element bounding boxes (for citations and bbox-grounded retrieval) from a single parse.

The C# / .NET ecosystem currently has no equivalent — PdfStruct is the gap-filler.

## Licensing

Apache-2.0 overall, with one exception: `src/PdfStruct/Analysis/FontBasedElementClassifier.cs` carries derived numeric constants from veraPDF wcag algorithms (dual-licensed GPLv3+ / MPLv2+, taken under MPL-2.0) and is therefore dual-licensed Apache-2.0 / MPL-2.0. Any new code that incorporates additional veraPDF-derived material must keep the same per-file MPL marking and update `NOTICE` accordingly. All other algorithmic work in this repo is derived independently from public specifications, the PdfPig source (Apache-2.0), or first-principles experimentation against the local fixture set — do not introduce additional copyleft-derived material without flagging it.

## Layout

```
src/
  PdfStruct/         Library (NuGet target: PdfStruct)
    Models/          Document model: ContentElement, BoundingBox, etc.
    Analysis/        XyCutLayoutAnalyzer, FontBasedElementClassifier (probabilistic),
                     RegexHeadingClassifier (pattern-driven), CompositeElementClassifier,
                     DocumentStatistics + RarityTable, RunningFurnitureDetector
    Rendering/       Markdown + JSON renderers
    Safety/          Prompt-injection filter, text sanitizer
    PdfStructParser  Public entry point
  PdfStruct.Cli/     Console app with `extract` and `diagnose` verbs, packaged later
                     as `dotnet tool` (command name `pdfstruct`)
  PdfStruct.Tests/
    Fixtures/        PDFs that ship with the repo (must be copyright-clean) + Korean fixture pattern helpers
playground/          Gitignored sandbox for local user PDFs (see playground/README.md)
todo/                Local planning notes (gitignored)
```

The CLI is a thin shell over the library — keep it that way. Anything reusable goes into the library; the CLI handles arg parsing, IO, and debug-image rendering only.

## Build, test, format

```bash
dotnet restore PdfStruct.sln
dotnet build PdfStruct.sln -c Release --no-restore
dotnet test PdfStruct.sln -c Release --no-build
dotnet format PdfStruct.sln --verify-no-changes --no-restore
```

Target framework: `net8.0` (pinned via `global.json`). C# language version is `12.0` (pinned via `Directory.Build.props`). Package versions are centrally managed in `Directory.Packages.props`.

## Running the CLI locally

```bash
dotnet run --project src/PdfStruct.Cli -- extract playground/some.pdf
dotnet run --project src/PdfStruct.Cli -- extract playground/some.pdf -o out.md
dotnet run --project src/PdfStruct.Cli -- extract playground/some.pdf --debug-image out/debug
```

Drop test PDFs into `playground/` (gitignored). The build also copies the apphost to `pdfstruct.exe` next to `PdfStruct.Cli.exe` so you can invoke `bin/.../pdfstruct.exe` directly.

## Conventions

- **XML doc comments**: every public type and method gets a full `<summary>`/`<param>`/`<returns>`/`<exception>` block. Private methods get at least a one-line `<summary>`. Keep the style concise — match what's already in `PdfStructParser.cs` and `Renderers.cs`.
- **No comments that explain *what* the code does** — well-named identifiers do that. Comments only for non-obvious *why*: a hidden constraint, a workaround, an invariant a reader would otherwise miss.
- **No backwards-compatibility shims, removed-code markers, or "// TODO future" placeholders.** If something is unused, delete it.
- **JSON shape is a public contract.** The renderer uses key names with spaces (`"file name"`, `"bounding box"`, `"page number"`, etc.) and element typing (`level: "Title"` plus `heading level: 1`). Don't unilaterally rename keys, change `bounding box` from array to object, or remove duplicated fields — they look redundant but downstream consumers depend on them. If you genuinely need a different JSON shape, add it as an option, don't break the default.
- **Date/time fields** in JSON should serialize as ISO 8601 (`2026-04-30T11:30:09+09:00`), not the PDF raw `D:YYYYMMDDhhmmss±HH'mm'` form.
- **Heading classification**: language-specific heuristics (e.g., Korean legal patterns like `제N장`, `전문`, `부칙`) belong in their own `IElementClassifier` strategy, not inlined into `FontBasedElementClassifier`. Compose classifiers; don't stack heuristics into one class.
- **CLI executable naming**: do not set `<AssemblyName>pdfstruct</AssemblyName>` on `PdfStruct.Cli.csproj` — it case-insensitively collides with the library's `PdfStruct.dll` and breaks `GenerateDepsFile`. The post-build `Copy` target in the csproj produces `pdfstruct.exe` alongside the original output. The eventual `dotnet tool` packaging will use `<ToolCommandName>pdfstruct</ToolCommandName>` for the launcher.

## Fixtures

`src/PdfStruct.Tests/Fixtures/` holds copyright-clean PDFs that ship with the repo. The primary regression fixture is `kr_constitution.pdf` (Constitution of Korea, public domain under Article 7 of the Korean Copyright Act). It is uniquely useful for layout-analysis testing because:

- Article numbers `제1조` ... `제130조` form a strict monotonic sequence — perfect for reading-order regression assertions.
- Section markers (`제N편`, `제N장`, `제N절`, `제N관`) form a clean four-level heading hierarchy.
- Running headers (`대한민국헌법`) and footers (`법제처 N 국가법령정보센터`) repeat across all 14 pages — exercise the running-header detector.
- Justified single-column body produces orphan tail lines (e.g. `한다.`) that exercise paragraph-merge logic.

When adding a new fixture, follow the source/license rules in `src/PdfStruct.Tests/Fixtures/README.md`. Never commit copyrighted scans, marketing PDFs, or web-scraped content.

For PDFs that are private, large, or uncertain copyright-wise, drop them into `playground/` instead — that directory is gitignored.

## Status

PdfStruct is **early alpha**. The public API and JSON output schema may still change before the first stable release. Be willing to break things now — schema break cost rises sharply after v0.1 alpha public.

## What "done" means here

- The change builds clean (`dotnet build` with no warnings on Release).
- Tests pass (`dotnet test`).
- Format check passes (`dotnet format --verify-no-changes`).
- For library changes that affect output: run the CLI against `src/PdfStruct.Tests/Fixtures/kr_constitution.pdf` and eyeball the Markdown / JSON output. Layout regressions are not always caught by unit tests yet.
- XML doc comments are present on every new public/internal API, with `<summary>` at minimum on private members.

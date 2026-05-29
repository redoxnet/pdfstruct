# PdfStruct

## v0.1.0-alpha.1

First alpha. RAG-optimized structured extraction from PDFs for .NET — produces
Markdown (for LLM context) and structured JSON with per-element bounding boxes
(for citations and bbox-grounded retrieval) from a single parse.

- `PdfStruct` — the library (NuGet: `PdfStruct`).
- `PdfStruct.Cli` — the command-line tool (NuGet: `PdfStruct.Cli`, command
  `pdfstruct`), with `extract` and `diagnose` verbs.

The public API and JSON output schema may still change before the first stable
release.

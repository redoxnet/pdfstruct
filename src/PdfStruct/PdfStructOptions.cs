// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Safety;

namespace PdfStruct;

/// <summary>
/// Specifies the output format for PDF conversion.
/// </summary>
[Flags]
public enum OutputFormat
{
    /// <summary>Structured Markdown output for LLM context and RAG chunking.</summary>
    Markdown = 1,
    /// <summary>Structured JSON with per-element bounding boxes.</summary>
    Json = 2,
    /// <summary>Both Markdown and JSON.</summary>
    Both = Markdown | Json
}

/// <summary>
/// Specifies how images should be handled during extraction.
/// </summary>
public enum ImageOutputMode
{
    /// <summary>Do not extract images.</summary>
    Off,
    /// <summary>Embed images as Base64 data URIs.</summary>
    Embedded,
    /// <summary>Save images as external files.</summary>
    External
}

/// <summary>
/// Configuration options for <see cref="PdfStructParser"/>.
/// </summary>
public sealed class PdfStructOptions
{
    /// <summary>Gets or sets the output format(s). Default: Markdown.</summary>
    public OutputFormat Format { get; set; } = OutputFormat.Markdown;

    /// <summary>Gets or sets image handling mode. Default: Off.</summary>
    public ImageOutputMode ImageOutput { get; set; } = ImageOutputMode.Off;

    /// <summary>Gets or sets the image format ("png" or "jpeg"). Default: "png".</summary>
    public string ImageFormat { get; set; } = "png";

    /// <summary>
    /// Gets or sets whether to detect regions of vector path graphics (charts,
    /// flowcharts, diagrams) and emit them as vector <see cref="Models.FigureElement"/>s.
    /// Has effect only when <see cref="ImageOutput"/> is not
    /// <see cref="ImageOutputMode.Off"/>. Default: true.
    /// </summary>
    public bool DetectVectorFigures { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to detect borderless tables (aligned text columns
    /// with no ruling lines) and emit them as <see cref="Models.TableElement"/>s.
    /// Runs before paragraph assembly; detected table lines are removed from the
    /// paragraph stream. Default: true.
    /// </summary>
    public bool DetectTables { get; set; } = true;

    /// <summary>
    /// Gets or sets the directory that extracted image files are written to when
    /// <see cref="ImageOutput"/> is <see cref="ImageOutputMode.External"/>. When
    /// <c>null</c>, external mode emits image elements without a file (structure
    /// and bounding box only). Ignored for <see cref="ImageOutputMode.Embedded"/>.
    /// </summary>
    public string? ImageOutputDirectory { get; set; }

    /// <summary>Gets or sets whether to use Tagged PDF structure tree when available. Default: true.</summary>
    public bool UseStructTree { get; set; } = true;

    /// <summary>Gets or sets whether to filter hidden text for prompt injection protection. Default: true.</summary>
    public bool FilterHiddenText { get; set; } = true;

    /// <summary>Gets or sets whether to mask common sensitive values in extracted text. Default: false.</summary>
    public bool SanitizeText { get; set; }

    /// <summary>
    /// Gets or sets the replacement for invalid extraction characters such as U+FFFD and NUL.
    /// Set to <c>null</c> to preserve them. Default: space.
    /// </summary>
    public string? InvalidCharacterReplacement { get; set; } = " ";

    /// <summary>Gets the regex-based sanitization rules used when <see cref="SanitizeText"/> is enabled.</summary>
    public List<TextSanitizationRule> SanitizationRules { get; } = TextSanitizer.CreateDefaultRules();

    /// <summary>Gets or sets whether to exclude headers/footers. Default: true.</summary>
    public bool ExcludeHeadersFooters { get; set; } = true;

    /// <summary>
    /// Gets or sets the legacy minimum horizontal gap ratio. Retained for API
    /// compatibility; the current XY-Cut analyzer uses an absolute gap
    /// threshold in PDF points.
    /// </summary>
    public double MinGapRatioX { get; set; } = 0.01;

    /// <summary>
    /// Gets or sets the legacy minimum vertical gap ratio. Retained for API
    /// compatibility; the current XY-Cut analyzer uses an absolute gap
    /// threshold in PDF points.
    /// </summary>
    public double MinGapRatioY { get; set; } = 0.005;

    /// <summary>
    /// Gets or sets the heading-probability threshold used by the default
    /// <see cref="Analysis.FontBasedElementClassifier"/>. Blocks scoring
    /// above this value are classified as headings. Default: 0.75.
    /// </summary>
    public double HeadingProbabilityThreshold { get; set; } = 0.75;
}

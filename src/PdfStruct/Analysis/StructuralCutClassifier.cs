// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using UglyToad.PdfPig.Core;

namespace PdfStruct.Analysis;

/// <summary>
/// Classifies whitespace rectangles returned by PdfPig's
/// <c>WhitespaceCoverExtractor</c> into candidate structural cuts:
/// vertical (column gutters) and horizontal (paragraph or section breaks).
/// The criteria are page-relative so they generalise across page sizes
/// and document classes (technical reports, articles, two-column patents,
/// single-column legal text).
/// </summary>
/// <remarks>
/// Thresholds are tuned empirically against the fixtures in
/// <c>playground/_bench</c>; the bench-segmenter CLI verb renders the
/// classification visually so criteria can be re-tuned by inspection
/// rather than by guessing. Both classifiers are pure predicates so
/// callers can run <c>WhitespaceCoverExtractor.GetWhitespaces</c> once
/// and partition the output without re-running the cover algorithm.
/// </remarks>
public static class StructuralCutClassifier
{
    /// <summary>
    /// Whitespace must span at least this fraction of the page height
    /// to be considered a candidate vertical (column) cut.
    /// </summary>
    public const double VerticalCutMinHeightRatio = 0.5;

    /// <summary>
    /// Whitespace wider than this fraction of the page width cannot
    /// be a vertical gutter; wider gaps are typically margins or
    /// inter-paragraph horizontal breaks.
    /// </summary>
    public const double VerticalCutMaxWidthRatio = 0.1;

    /// <summary>
    /// Whitespace must span at least this fraction of the page width
    /// to be considered a candidate horizontal (paragraph or section)
    /// break.
    /// </summary>
    public const double HorizontalCutMinWidthRatio = 0.4;

    /// <summary>
    /// Whitespace shorter than this absolute height (PDF points) is
    /// ignored as a horizontal cut — gaps in this range are usually
    /// single-line spacing rather than paragraph breaks.
    /// </summary>
    public const double HorizontalCutMinHeightPoints = 8.0;

    /// <summary>
    /// Returns true when <paramref name="whitespace"/> has the shape
    /// of a column gutter: tall enough to span at least
    /// <see cref="VerticalCutMinHeightRatio"/> of the page height and
    /// narrow enough not to be a margin or full-width gap.
    /// </summary>
    /// <param name="whitespace">A whitespace rectangle returned by WhitespaceCoverExtractor.</param>
    /// <param name="pageWidth">The page width in PDF points.</param>
    /// <param name="pageHeight">The page height in PDF points.</param>
    public static bool IsVerticalCutCandidate(PdfRectangle whitespace, double pageWidth, double pageHeight)
    {
        return whitespace.Width > 0
            && whitespace.Height >= pageHeight * VerticalCutMinHeightRatio
            && whitespace.Width <= pageWidth * VerticalCutMaxWidthRatio;
    }

    /// <summary>
    /// Returns true when <paramref name="whitespace"/> has the shape
    /// of a major horizontal break: wide enough to cross at least
    /// <see cref="HorizontalCutMinWidthRatio"/> of the page and tall
    /// enough (≥ <see cref="HorizontalCutMinHeightPoints"/>) to not be
    /// a single-line gap. Rectangles that already qualify as vertical
    /// cuts are rejected to avoid double-classifying page-spanning
    /// margins.
    /// </summary>
    /// <param name="whitespace">A whitespace rectangle returned by WhitespaceCoverExtractor.</param>
    /// <param name="pageWidth">The page width in PDF points.</param>
    /// <param name="pageHeight">The page height in PDF points.</param>
    public static bool IsHorizontalCutCandidate(PdfRectangle whitespace, double pageWidth, double pageHeight)
    {
        if (IsVerticalCutCandidate(whitespace, pageWidth, pageHeight)) return false;
        return whitespace.Height > 0
            && whitespace.Width >= pageWidth * HorizontalCutMinWidthRatio
            && whitespace.Height >= HorizontalCutMinHeightPoints;
    }

    /// <summary>
    /// Returns true when <paramref name="whitespace"/> is a clean
    /// between-rows gap: no obstacle's vertical extent overlaps with
    /// the rectangle's vertical extent. Filters out side-rectangles
    /// (the whitespace beside a short orphan-tail line, for example)
    /// whose Y range coincides with a text line and would otherwise
    /// be mistaken for a paragraph-separating horizontal cut.
    /// </summary>
    /// <param name="whitespace">A candidate horizontal cut rectangle.</param>
    /// <param name="obstacleBoxes">All word (or other obstacle) bounding boxes on the page.</param>
    public static bool IsCleanHorizontalGap(PdfRectangle whitespace, IEnumerable<PdfRectangle> obstacleBoxes)
    {
        foreach (var box in obstacleBoxes)
        {
            if (box.Top > whitespace.Bottom && box.Bottom < whitespace.Top)
                return false;
        }
        return true;
    }
}

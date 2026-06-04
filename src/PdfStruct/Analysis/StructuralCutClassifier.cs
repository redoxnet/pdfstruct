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
    /// to be considered a candidate vertical (column) cut. The figure is a
    /// tuning constant, not a hard boundary: it sits below the common
    /// partial-height sidebar — a full-width title/abstract header over a
    /// two-column body leaves the gutter spanning ≈45% of the page on journal
    /// article first pages (PLOS) — yet high enough to keep equation-internal
    /// and orphan-tail channels, which are shorter, out.
    /// </summary>
    public const double VerticalCutMinHeightRatio = 0.4;

    /// <summary>
    /// Whitespace wider than this fraction of the page width cannot
    /// be a vertical gutter; wider gaps are typically margins or
    /// inter-paragraph horizontal breaks.
    /// </summary>
    public const double VerticalCutMaxWidthRatio = 0.1;

    /// <summary>
    /// Vertical cuts whose horizontal centre is closer than this
    /// fraction of the page width to either side margin are rejected.
    /// Real column gutters sit between content on both sides; a tall
    /// narrow strip near a margin is almost always a hanging-indent
    /// gap or a list-label gutter rather than a column boundary
    /// (plos_game_based_education page 13: the references hanging-
    /// indent gap reaches ~9pt and sits at 8% of the page width).
    /// </summary>
    public const double VerticalCutMarginExclusionRatio = 0.2;

    /// <summary>
    /// Width threshold (PDF points) separating "wide" gutters from
    /// "narrow" ones for the pairing rule in
    /// <see cref="EnforceNarrowPairing"/>. Gutters in single-line-
    /// width (1pt rule) column layouts run 15-30pt; gutters that
    /// flank a narrow centre column (US-patent line-number strips)
    /// run 4-6pt; hanging-indent gaps inside a column run 5-9pt.
    /// 10pt cleanly separates "obvious column boundary" from
    /// "needs further justification".
    /// </summary>
    public const double NarrowGutterWidthPoints = 10.0;

    /// <summary>
    /// Two narrow gutters whose centres are within this distance
    /// (PDF points) are considered a pair flanking a narrow centre
    /// column. The 40pt span comfortably accommodates the centre
    /// line-number columns on US patent bodies (~30pt apart) without
    /// pairing genuinely unrelated narrow strips across the page.
    /// </summary>
    public const double NarrowPairingMaxDistancePoints = 40.0;

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
        if (whitespace.Width <= 0) return false;
        if (whitespace.Width > pageWidth * VerticalCutMaxWidthRatio) return false;
        if (whitespace.Height < pageHeight * VerticalCutMinHeightRatio) return false;

        var centerX = (whitespace.Left + whitespace.Right) / 2.0;
        var marginExclusion = pageWidth * VerticalCutMarginExclusionRatio;
        if (centerX < marginExclusion || centerX > pageWidth - marginExclusion) return false;

        return true;
    }

    /// <summary>
    /// Removes narrow (<see cref="NarrowGutterWidthPoints"/>) vertical
    /// cuts that do not have another narrow neighbour within
    /// <see cref="NarrowPairingMaxDistancePoints"/> on the X axis.
    /// Wide gutters are kept unconditionally. The rule encodes the
    /// observation that genuinely-narrow column boundaries appear in
    /// pairs flanking a narrow centre column (the line-number strip
    /// on US patent bodies), while isolated narrow strips inside a
    /// column (hanging-indent label-vs-body gaps on academic-paper
    /// references) sit alone next to wide gutters and have no narrow
    /// partner.
    /// </summary>
    /// <param name="candidates">Vertical cuts that already passed
    /// <see cref="IsVerticalCutCandidate"/>, in any order.</param>
    /// <returns>Filtered cuts in the input order, with unpaired narrow
    /// cuts removed.</returns>
    public static List<PdfRectangle> EnforceNarrowPairing(IReadOnlyList<PdfRectangle> candidates)
    {
        if (candidates.Count == 0) return [];

        var centers = candidates.Select(c => (c.Left + c.Right) / 2.0).ToList();
        var result = new List<PdfRectangle>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Width >= NarrowGutterWidthPoints)
            {
                result.Add(candidates[i]);
                continue;
            }

            var paired = false;
            for (var j = 0; j < candidates.Count; j++)
            {
                if (i == j) continue;
                if (candidates[j].Width >= NarrowGutterWidthPoints) continue;
                if (Math.Abs(centers[i] - centers[j]) <= NarrowPairingMaxDistancePoints)
                {
                    paired = true;
                    break;
                }
            }
            if (paired) result.Add(candidates[i]);
        }

        return result;
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

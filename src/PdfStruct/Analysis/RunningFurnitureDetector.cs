// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Detects elements that repeat as page headers or page footers across the
/// document — running titles, page numbers, source-credit lines — so the
/// extraction pipeline can filter them out of the main content stream.
/// </summary>
/// <remarks>
/// Candidates are restricted to the top <see cref="HeaderBandTopRatio"/>
/// or bottom <see cref="FooterBandBottomRatio"/> of their page, content is
/// normalized so that varying digit runs (page numbers, dates) still match
/// across pages, and groups appearing on at least
/// <see cref="RepeatRatioThreshold"/> of the document's pages are flagged
/// as running furniture.
/// </remarks>
public static partial class RunningFurnitureDetector
{
    /// <summary>
    /// Header candidates must have their bottom edge above this ratio of
    /// page height (default <c>0.25</c> — element lies in the top 25% of
    /// the page). The narrow band keeps body paragraphs out of the
    /// candidate set even on short pages.
    /// </summary>
    public const double HeaderBandTopRatio = 0.25;

    /// <summary>
    /// Footer candidates must have their top edge below this ratio of page
    /// height (default <c>0.25</c> — element lies in the bottom 25% of the
    /// page).
    /// </summary>
    public const double FooterBandBottomRatio = 0.25;

    /// <summary>Minimum fraction of pages on which an element group must appear to be flagged as repeating furniture.</summary>
    public const double RepeatRatioThreshold = 0.70;

    /// <summary>
    /// Identifies element IDs that repeat as headers or footers across pages.
    /// </summary>
    /// <param name="elements">All classified content elements in the document, in any order.</param>
    /// <param name="pageHeights">Map from 1-indexed page number to page height in PDF points.</param>
    /// <returns>The set of element IDs that should be filtered out as running furniture.</returns>
    public static IReadOnlySet<int> DetectRepeatingIds(
        IReadOnlyList<ContentElement> elements,
        IReadOnlyDictionary<int, double> pageHeights)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(pageHeights);

        var totalPages = pageHeights.Count;
        if (totalPages < 3) return new HashSet<int>();

        var minPagesForRepeat = Math.Max(2, (int)Math.Ceiling(totalPages * RepeatRatioThreshold));

        var candidates = new List<Candidate>();
        foreach (var element in elements)
        {
            if (!pageHeights.TryGetValue(element.PageNumber, out var pageHeight) || pageHeight <= 0) continue;

            var band = ClassifyBand(element.BoundingBox, pageHeight);
            if (band is null) continue;

            var content = ContentOf(element);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var quantisedLeft = Math.Round(element.BoundingBox.Left / 10.0) * 10.0;
            candidates.Add(new Candidate(
                ElementId: element.Id,
                PageNumber: element.PageNumber,
                Band: band.Value,
                NormalizedText: Normalize(content),
                QuantisedLeft: quantisedLeft));
        }

        // Group by position too (10pt buckets) so a centred document title and
        // a top-right running header that share the same text remain in
        // separate groups. Heading-classified elements are intentionally not
        // skipped: a header/footer-band element that repeats on 70%+ of pages
        // is furniture regardless of its classification, and the upstream
        // template-class promotion can lift such repeats into the heading
        // set en masse.
        return candidates
            .GroupBy(c => (c.Band, c.NormalizedText, c.QuantisedLeft))
            .Where(g => g.Select(c => c.PageNumber).Distinct().Count() >= minPagesForRepeat)
            .SelectMany(g => g.Select(c => c.ElementId))
            .ToHashSet();
    }

    /// <summary>Classifies a block's vertical position into a header/footer band, or returns <c>null</c> when it lies in the body.</summary>
    private static FurnitureBand? ClassifyBand(BoundingBox bbox, double pageHeight)
    {
        var bottomRatio = bbox.Bottom / pageHeight;
        var topRatio = bbox.Top / pageHeight;

        if (topRatio < FooterBandBottomRatio) return FurnitureBand.Footer;
        if (bottomRatio > 1.0 - HeaderBandTopRatio) return FurnitureBand.Header;
        return null;
    }

    /// <summary>Strips per-page-variable substrings (digit runs, common date glyphs) so the same furniture matches across pages.</summary>
    private static string Normalize(string content) =>
        DigitRun().Replace(content.Trim(), "#");

    /// <summary>Returns the text content of a content element, or an empty string for non-text element types.</summary>
    private static string ContentOf(ContentElement element) => element switch
    {
        ParagraphElement p => p.Text.Content,
        CaptionElement c => c.Text.Content,
        HeaderFooterElement => string.Empty,
        _ => string.Empty
    };

    [GeneratedRegex(@"\d+", RegexOptions.Compiled)]
    private static partial Regex DigitRun();

    private readonly record struct Candidate(int ElementId, int PageNumber, FurnitureBand Band, string NormalizedText, double QuantisedLeft);

    /// <summary>Page-furniture spatial band.</summary>
    private enum FurnitureBand { Header, Footer }
}

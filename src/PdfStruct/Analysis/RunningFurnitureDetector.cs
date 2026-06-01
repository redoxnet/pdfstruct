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
/// or bottom <see cref="FooterBandBottomRatio"/> of their page and then
/// grouped by a position-and-style signature: the same normalized text, at
/// the same horizontal bucket (<see cref="LeftBucketPoints"/>), the same
/// vertical band bucket (<see cref="TopBucketRatio"/>), and the same font
/// size bucket (<see cref="FontSizeBucketPoints"/>). A group is treated as
/// running furniture when any of three signals fires — broad page coverage
/// (<see cref="RepeatRatioThreshold"/>), a page-number sequence across its
/// occurrences, or an exact repeat on adjacent / alternating pages — so the
/// detector handles long documents, two-page documents, and even/odd
/// (left/right) layouts alike. The grouping signature folds in font size and
/// vertical position so a centred document title and a smaller running header
/// that happen to share text never collapse into one group.
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

    /// <summary>Minimum fraction of pages on which an element group must appear to be flagged as repeating furniture by the broad-coverage signal.</summary>
    public const double RepeatRatioThreshold = 0.70;

    /// <summary>Horizontal quantisation, in PDF points, used to bucket candidate left edges so near-aligned occurrences share a group.</summary>
    public const double LeftBucketPoints = 10.0;

    /// <summary>Vertical quantisation, as a fraction of page height, used to bucket candidate top edges so occurrences at the same band share a group.</summary>
    public const double TopBucketRatio = 0.02;

    /// <summary>Font-size quantisation, in points, used to bucket candidate font sizes so a running header and a same-text body line of a different size do not group together.</summary>
    public const double FontSizeBucketPoints = 1.0;

    /// <summary>Maximum page distance for the adjacency signal — pages this far apart (consecutive or one-apart) count as a nearby pair, mirroring ODL's increment-1 and increment-2 comparison.</summary>
    public const int MaxAdjacentPageGap = 2;

    /// <summary>Minimum number of nearby page pairs an exact-repeat group must span before the adjacency signal fires. Two pairs means three consecutive pages, an alternating run, or two separate clusters — a lone adjacent pair is treated as coincidence.</summary>
    public const int MinNearbyPairs = 2;

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
        if (totalPages < 2) return new HashSet<int>();

        var candidates = new List<Candidate>();
        foreach (var element in elements)
        {
            if (!pageHeights.TryGetValue(element.PageNumber, out var pageHeight) || pageHeight <= 0) continue;

            var band = ClassifyBand(element.BoundingBox, pageHeight);
            if (band is null) continue;

            var content = ContentOf(element);
            if (string.IsNullOrWhiteSpace(content)) continue;

            candidates.Add(new Candidate(
                ElementId: element.Id,
                PageNumber: element.PageNumber,
                Band: band.Value,
                RawText: content.Trim(),
                NormalizedText: Normalize(content),
                LeftBucket: LeftBucket(element.BoundingBox.Left),
                TopBucket: TopBucket(element.BoundingBox.Top, pageHeight),
                FontSizeBucket: FontSizeBucket(FontSizeOf(element))));
        }

        var rejected = new HashSet<int>();
        foreach (var group in candidates.GroupBy(c => c.Signature))
        {
            var occurrences = group
                .Select(c => (c.PageNumber, c.RawText))
                .ToList();
            if (IsRepeatingFurniture(occurrences, totalPages))
            {
                foreach (var candidate in group)
                    rejected.Add(candidate.ElementId);
            }
        }

        return rejected;
    }

    /// <summary>
    /// Decides whether a group of candidate occurrences that share a
    /// position-and-style signature is running furniture. The group qualifies
    /// when any one of three independent signals holds: it appears on at least
    /// <see cref="RepeatRatioThreshold"/> of the document's pages; the varying
    /// digits across its occurrences form a page-number sequence (the printed
    /// number tracks the page number); or its text repeats verbatim across at
    /// least <see cref="MinNearbyPairs"/> page pairs no more than
    /// <see cref="MaxAdjacentPageGap"/> apart. The exact-repeat signal
    /// deliberately ignores a lone adjacent pair — two repeats are weak
    /// evidence and the coarse position-and-style signature can collide — so a
    /// single pair must be corroborated either by a third nearby page (three
    /// consecutive pages yield two pairs) or by a second cluster elsewhere
    /// (the two-page document case is already covered by the coverage signal).
    /// A single occurrence is never furniture.
    /// </summary>
    /// <param name="occurrences">The (page number, raw text) pairs that fell into one signature group; multiple entries may share a page.</param>
    /// <param name="totalPages">Total number of pages in the document, used to scale the broad-coverage threshold.</param>
    /// <returns><c>true</c> when the group should be treated as running furniture.</returns>
    internal static bool IsRepeatingFurniture(
        IReadOnlyList<(int Page, string RawText)> occurrences,
        int totalPages)
    {
        var distinctPages = occurrences.Select(o => o.Page).Distinct().OrderBy(p => p).ToList();
        if (distinctPages.Count < 2) return false;

        var minPagesForRepeat = Math.Max(2, (int)Math.Ceiling(totalPages * RepeatRatioThreshold));
        if (distinctPages.Count >= minPagesForRepeat) return true;

        if (IsPageNumberSequence(occurrences)) return true;

        if (AllRawTextEqual(occurrences) && CountNearbyPairs(distinctPages) >= MinNearbyPairs) return true;

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the digits that vary across a group's
    /// occurrences form a genuine page-number sequence: some digit-run slot
    /// whose value tracks the page number one-for-one, i.e.
    /// <c>value − pageNumber</c> is the same constant for every occurrence.
    /// The constant offset admits front matter (printed "1" first appearing on
    /// a later physical page) while rejecting numbers that merely increase —
    /// "10, 20, 30" on consecutive pages steps by ten, not by one, so it is
    /// not a page number. This matches ODL, which ties the label increment to
    /// the page increment. Recognises footers like
    /// <c>"법제처 1 국가법령정보센터"</c>, <c>"법제처 2 국가법령정보센터"</c>
    /// as furniture even when they cover too few pages for the broad-coverage
    /// signal.
    /// </summary>
    /// <param name="occurrences">The (page number, raw text) pairs of one signature group.</param>
    /// <returns><c>true</c> when a digit slot tracks the page number at a constant offset across at least two distinct pages.</returns>
    internal static bool IsPageNumberSequence(IReadOnlyList<(int Page, string RawText)> occurrences)
    {
        if (occurrences.Count < 2) return false;

        var ordered = occurrences.OrderBy(o => o.Page).ToList();
        var runsPerItem = ordered
            .Select(o => DigitRun().Matches(o.RawText).Select(m => m.Value).ToList())
            .ToList();

        var slotCount = runsPerItem[0].Count;
        if (slotCount == 0) return false;
        if (runsPerItem.Any(runs => runs.Count != slotCount)) return false;

        for (var slot = 0; slot < slotCount; slot++)
        {
            long? offset = null;
            var consistent = true;
            var sawDistinctPages = false;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!long.TryParse(runsPerItem[i][slot], out var value))
                {
                    consistent = false;
                    break;
                }
                var currentOffset = value - ordered[i].Page;
                if (offset is null)
                {
                    offset = currentOffset;
                }
                else if (currentOffset != offset.Value)
                {
                    consistent = false;
                    break;
                }
                if (i > 0 && ordered[i].Page != ordered[i - 1].Page)
                    sawDistinctPages = true;
            }
            if (consistent && sawDistinctPages) return true;
        }
        return false;
    }

    /// <summary>Counts the distinct-page pairs lying within <see cref="MaxAdjacentPageGap"/> of each other in an ascending page list.</summary>
    private static int CountNearbyPairs(IReadOnlyList<int> orderedDistinctPages)
    {
        var count = 0;
        for (var i = 0; i < orderedDistinctPages.Count; i++)
        {
            for (var j = i + 1; j < orderedDistinctPages.Count; j++)
            {
                if (orderedDistinctPages[j] - orderedDistinctPages[i] <= MaxAdjacentPageGap)
                    count++;
                else
                    break;
            }
        }
        return count;
    }

    /// <summary>Returns <c>true</c> when every occurrence carries the same trimmed raw text.</summary>
    private static bool AllRawTextEqual(IReadOnlyList<(int Page, string RawText)> occurrences)
    {
        var first = occurrences[0].RawText;
        return occurrences.All(o => string.Equals(o.RawText, first, StringComparison.Ordinal));
    }

    /// <summary>Quantises a left edge (PDF points) to a <see cref="LeftBucketPoints"/> grid for signature grouping.</summary>
    internal static long LeftBucket(double left) => (long)Math.Round(left / LeftBucketPoints);

    /// <summary>Quantises a top edge to a <see cref="TopBucketRatio"/> fraction-of-page-height grid for signature grouping.</summary>
    internal static long TopBucket(double top, double pageHeight) =>
        pageHeight > 0 ? (long)Math.Round(top / pageHeight / TopBucketRatio) : 0;

    /// <summary>Quantises a font size (PDF points) to a <see cref="FontSizeBucketPoints"/> grid for signature grouping.</summary>
    internal static long FontSizeBucket(double fontSize) => (long)Math.Round(fontSize / FontSizeBucketPoints);

    /// <summary>Classifies a block's vertical position into a header/footer band, or returns <c>null</c> when it lies in the body.</summary>
    private static FurnitureBand? ClassifyBand(BoundingBox bbox, double pageHeight)
    {
        var bottomRatio = bbox.Bottom / pageHeight;
        var topRatio = bbox.Top / pageHeight;

        if (topRatio < FooterBandBottomRatio) return FurnitureBand.Footer;
        if (bottomRatio > 1.0 - HeaderBandTopRatio) return FurnitureBand.Header;
        return null;
    }

    /// <summary>Strips per-page-variable substrings (digit runs) and folds whitespace so the same furniture matches across pages.</summary>
    internal static string Normalize(string content) =>
        Whitespace().Replace(DigitRun().Replace(content.Trim(), "#"), " ");

    /// <summary>Returns the text content of a content element, or an empty string for non-text element types.</summary>
    private static string ContentOf(ContentElement element) => element switch
    {
        ParagraphElement p => p.Text.Content,
        CaptionElement c => c.Text.Content,
        HeaderFooterElement => string.Empty,
        _ => string.Empty
    };

    /// <summary>Returns the font size of a text-bearing content element, or zero for non-text element types.</summary>
    private static double FontSizeOf(ContentElement element) => element switch
    {
        ParagraphElement p => p.Text.FontSize,
        HeadingElement h => h.Text.FontSize,
        CaptionElement c => c.Text.FontSize,
        _ => 0.0
    };

    [GeneratedRegex(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private readonly record struct Candidate(
        int ElementId,
        int PageNumber,
        FurnitureBand Band,
        string RawText,
        string NormalizedText,
        long LeftBucket,
        long TopBucket,
        long FontSizeBucket)
    {
        /// <summary>Position-and-style grouping signature: occurrences that share it are candidate copies of one running element.</summary>
        public (FurnitureBand, string, long, long, long) Signature =>
            (Band, NormalizedText, LeftBucket, TopBucket, FontSizeBucket);
    }

    /// <summary>Page-furniture spatial band.</summary>
    private enum FurnitureBand { Header, Footer }
}

// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Analysis;
using PdfStruct.Rendering;
using PdfStruct.Safety;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using PdfPigWhitespaceCover = UglyToad.PdfPig.DocumentLayoutAnalysis.WhitespaceCoverExtractor;

namespace PdfStruct;

/// <summary>
/// The result of a PDF conversion.
/// </summary>
/// <param name="Document">The parsed document model.</param>
/// <param name="Markdown">Markdown output, or <c>null</c> if not requested.</param>
/// <param name="Json">JSON output (OpenDataLoader-compatible), or <c>null</c> if not requested.</param>
public sealed record PdfStructResult(
    Models.PdfDocument Document,
    string? Markdown,
    string? Json);

/// <summary>
/// One row of heading-probability diagnostic output, produced by
/// <see cref="PdfStructParser.AnalyzeHeadingProbabilities"/>.
/// </summary>
/// <param name="PageNumber">1-indexed page number where the block appears.</param>
/// <param name="Block">The extracted text block, including font and layout signals.</param>
/// <param name="Breakdown">Per-signal contributions and total heading probability.</param>
/// <param name="ClassifiedAsHeading">Whether the total exceeds the configured threshold.</param>
public readonly record struct HeadingDiagnosticRow(
    int PageNumber,
    Analysis.TextBlock Block,
    Analysis.HeadingProbabilityBreakdown Breakdown,
    bool ClassifiedAsHeading);

/// <summary>
/// One row of text-line diagnostic output, produced before paragraph merging.
/// </summary>
/// <param name="PageNumber">1-indexed page number where the line appears.</param>
/// <param name="Line">The extracted text line, including its line-level bounding box and style signals.</param>
public readonly record struct TextLineDiagnosticRow(
    int PageNumber,
    Analysis.TextBlock Line);

/// <summary>
/// One row of page-block diagnostic output, produced after paragraph merging
/// and reading-order sorting but before classification. Used by the
/// <c>bench-segmenter</c> CLI verb and other tools that compare PdfStruct's
/// block partition against external page segmenters.
/// </summary>
/// <param name="PageNumber">1-indexed page number where the block appears.</param>
/// <param name="Block">The merged text block with its bounding box and style signals.</param>
public readonly record struct PageBlockDiagnosticRow(
    int PageNumber,
    Analysis.TextBlock Block);

/// <summary>
/// Main entry point for RAG-optimized PDF extraction.
/// Coordinates PdfPig → word grouping → XY-Cut++ reading order →
/// element classification → Markdown/JSON rendering.
/// </summary>
/// <example>
/// <code>
/// var parser = new PdfStructParser();
/// var result = parser.Parse("document.pdf");
/// Console.WriteLine(result.Markdown);
/// </code>
/// </example>
public sealed class PdfStructParser
{
    private readonly PdfStructOptions _options;
    private readonly ILayoutAnalyzer _layoutAnalyzer;
    private readonly IElementClassifier _classifier;

    /// <summary>Initializes with default options.</summary>
    public PdfStructParser() : this(new PdfStructOptions()) { }

    /// <summary>
    /// Initializes with the specified options. The default classifier is a
    /// <see cref="CompositeElementClassifier"/> wrapping a single
    /// <see cref="FontBasedElementClassifier"/>; callers that want to inject
    /// pattern-driven heading recognition (for example a
    /// <see cref="RegexHeadingClassifier"/> in front of the font model) use
    /// the constructor that accepts a custom classifier instance.
    /// </summary>
    public PdfStructParser(PdfStructOptions options)
    {
        _options = options;
        _layoutAnalyzer = new XyCutLayoutAnalyzer(options.MinGapRatioX, options.MinGapRatioY);
        _classifier = new CompositeElementClassifier(
            new FontBasedElementClassifier(options.HeadingProbabilityThreshold));
    }

    /// <summary>Initializes with custom analyzer and classifier.</summary>
    public PdfStructParser(
        PdfStructOptions options, ILayoutAnalyzer layoutAnalyzer, IElementClassifier classifier)
    {
        _options = options;
        _layoutAnalyzer = layoutAnalyzer;
        _classifier = classifier;
    }

    /// <summary>Parses a PDF file by path.</summary>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public PdfStructResult Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(filePath);
        return ParseInternal(pdf, Path.GetFileName(filePath));
    }

    /// <summary>Parses a PDF from a stream.</summary>
    public PdfStructResult Parse(Stream stream, string fileName = "document.pdf")
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(stream);
        return ParseInternal(pdf, fileName);
    }

    /// <summary>Parses a PDF from a byte array.</summary>
    public PdfStructResult Parse(byte[] bytes, string fileName = "document.pdf")
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return ParseInternal(pdf, fileName);
    }

    /// <summary>
    /// Runs the parser pipeline up to (but not through) classification and
    /// returns the per-block heading-probability breakdown produced by the
    /// default <see cref="FontBasedElementClassifier"/>. Intended for
    /// threshold calibration and false-positive diagnosis — emit the rows
    /// to CSV and inspect score distributions across fixtures.
    /// </summary>
    /// <param name="filePath">Path to the input PDF.</param>
    /// <returns>One row per block, in extraction order, with score components and the threshold-based classification.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public IReadOnlyList<HeadingDiagnosticRow> AnalyzeHeadingProbabilities(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(filePath);
        return AnalyzeHeadingProbabilitiesInternal(pdf);
    }

    /// <summary>
    /// Extracts text lines before paragraph merging. Intended for pipeline
    /// diagnostics and debug overlays; it does not affect the public JSON
    /// schema.
    /// </summary>
    /// <param name="filePath">Path to the input PDF.</param>
    /// <returns>One row per pre-paragraph text line, in page extraction order.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public IReadOnlyList<TextLineDiagnosticRow> AnalyzeTextLines(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(filePath);
        var rows = new List<TextLineDiagnosticRow>();
        for (var p = 1; p <= pdf.NumberOfPages; p++)
        {
            foreach (var line in ExtractPageTextLines(pdf.GetPage(p)))
                rows.Add(new TextLineDiagnosticRow(p, line.ToTextBlock()));
        }

        return rows;
    }

    /// <summary>
    /// Extracts per-page text blocks after paragraph merging and reading-order
    /// sorting, but before classification, list detection, and running-furniture
    /// removal. Intended for layout-comparison harnesses that pit PdfStruct's
    /// block partition against alternative page segmenters on a shared input.
    /// </summary>
    /// <param name="filePath">Path to the input PDF.</param>
    /// <returns>One row per merged block, in page extraction order.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public IReadOnlyList<PageBlockDiagnosticRow> AnalyzePageBlocks(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(filePath);
        var rows = new List<PageBlockDiagnosticRow>();
        for (var p = 1; p <= pdf.NumberOfPages; p++)
        {
            var page = pdf.GetPage(p);
            foreach (var block in ExtractPageBlocks(page))
                rows.Add(new PageBlockDiagnosticRow(p, block));
        }

        return rows;
    }

    /// <summary>Per-page extraction + neighbour-aware scoring for diagnostic output.</summary>
    private IReadOnlyList<HeadingDiagnosticRow> AnalyzeHeadingProbabilitiesInternal(UglyToad.PdfPig.PdfDocument pdf)
    {
        var documentBlocks = new List<DocumentTextBlock>();
        for (var p = 1; p <= pdf.NumberOfPages; p++)
        {
            var page = pdf.GetPage(p);
            foreach (var block in ExtractPageBlocks(page))
                documentBlocks.Add(new DocumentTextBlock(p, block, IsStatsOnly: false, PageWidth: page.Width));
        }

        var classifier = new FontBasedElementClassifier(_options.HeadingProbabilityThreshold);
        var entries = classifier.AnalyzeHeadings(documentBlocks);

        var rows = new List<HeadingDiagnosticRow>(entries.Count);
        foreach (var entry in entries)
        {
            var doc = documentBlocks[entry.Index];
            rows.Add(new HeadingDiagnosticRow(
                PageNumber: doc.PageNumber,
                Block: doc.Block,
                Breakdown: entry.Breakdown,
                ClassifiedAsHeading: entry.ClassifiedAsHeading));
        }
        return rows;
    }

    private PdfStructResult ParseInternal(UglyToad.PdfPig.PdfDocument pdf, string fileName)
    {
        var info = pdf.Information;
        var doc = new Models.PdfDocument
        {
            FileName = fileName,
            NumberOfPages = pdf.NumberOfPages,
            Author = info.Author,
            Title = info.Title,
            CreationDate = NormalizePdfDate(info.CreationDate),
            ModificationDate = NormalizePdfDate(info.ModifiedDate)
        };

        var pageLines = new Dictionary<int, IReadOnlyList<TextLineBlock>>(pdf.NumberOfPages);
        var pageGeometries = new Dictionary<int, PageGeometry>(pdf.NumberOfPages);
        var pageHeights = new Dictionary<int, double>(pdf.NumberOfPages);
        var pageGutters = new Dictionary<int, IReadOnlyList<PdfRectangle>>(pdf.NumberOfPages);
        for (var p = 1; p <= pdf.NumberOfPages; p++)
        {
            var page = pdf.GetPage(p);
            pageGeometries[p] = new PageGeometry(page.Width, page.Height);
            pageHeights[p] = page.Height;
            pageLines[p] = ExtractPageTextLines(page);
            pageGutters[p] = DetectColumnGutters(page, _options.FilterHiddenText);
        }

        if (_options.ExcludeHeadersFooters)
            pageLines = FilterRunningFurnitureLines(pageLines, pageGeometries);

        var originalPageLines = pageLines.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TextLineBlock>)pair.Value.ToList());

        var pageLists = DetectListsPerPage(pageLines);

        if (pageLists.Count > 0)
            ApplyConservativeReconciliation(pageLines, originalPageLines, pageLists);

        var pageBlocks = new Dictionary<int, IReadOnlyList<TextBlock>>(pdf.NumberOfPages);
        var statsOnlyBlocks = new List<DocumentTextBlock>();
        for (var p = 1; p <= pdf.NumberOfPages; p++)
        {
            PageGeometry? pageGeometry = _options.ExcludeHeadersFooters ? pageGeometries[p] : null;
            var blocks = BuildPageBlocks(pageLines[p], pageGeometry, pageGutters[p]);

            if (pageLists.TryGetValue(p, out var listsOnPage))
            {
                foreach (var list in listsOnPage)
                    foreach (var item in list.Items)
                        statsOnlyBlocks.Add(new DocumentTextBlock(
                            p, SynthesizeListItemStatsBlock(list, item), IsStatsOnly: true));

                var augmented = new List<TextBlock>(blocks.Count + listsOnPage.Count);
                augmented.AddRange(blocks);
                for (var i = 0; i < listsOnPage.Count; i++)
                    augmented.Add(MakeListPlaceholder(listsOnPage[i], p, i));
                var ordered = _layoutAnalyzer.DetermineReadingOrder(augmented);
                blocks = WithStandaloneFlag(ordered);
            }

            pageBlocks[p] = blocks;
        }

        var totalCount = statsOnlyBlocks.Count;
        foreach (var blocks in pageBlocks.Values) totalCount += blocks.Count;
        var documentBlocks = new List<DocumentTextBlock>(totalCount);
        for (var p = 1; p <= pdf.NumberOfPages; p++)
        {
            var pageWidth = pageGeometries[p].Width;
            foreach (var block in pageBlocks[p])
                documentBlocks.Add(new DocumentTextBlock(p, block, IsStatsOnly: false, PageWidth: pageWidth));
        }
        documentBlocks.AddRange(statsOnlyBlocks);

        var elementId = 1;
        var elements = _classifier.Classify(documentBlocks, ref elementId);
        doc.Kids.AddRange(elements);

        if (pageLists.Count > 0)
            ReplaceListPlaceholders(doc.Kids, pageLists, originalPageLines);

        TemplateClassConsistency.PromoteSharedTemplates(doc.Kids);

        var pageWidths = pageGeometries.ToDictionary(pair => pair.Key, pair => pair.Value.Width);
        AssignHeadingLevels(doc.Kids, pageWidths);

        if (_options.ExcludeHeadersFooters)
        {
            var repeatingIds = RunningFurnitureDetector.DetectRepeatingIds(doc.Kids, pageHeights);
            if (repeatingIds.Count > 0)
                doc.Kids.RemoveAll(e => repeatingIds.Contains(e.Id));
        }

        RenumberElements(doc.Kids);

        string? markdown = _options.Format.HasFlag(OutputFormat.Markdown)
            ? new MarkdownRenderer().Render(doc) : null;
        string? json = _options.Format.HasFlag(OutputFormat.Json)
            ? new JsonRenderer().Render(doc) : null;

        return new PdfStructResult(doc, markdown, json);
    }

    /// <summary>
    /// Assigns numeric heading levels 1..N to <see cref="Models.HeadingElement"/>
    /// instances by clustering them on typographic and layout style
    /// (font size, font name, derived bold flag, indent bucket, page
    /// alignment) and ordering the resulting groups from largest/heaviest to
    /// smallest/lightest. Levels are uncapped on the data model; the Markdown
    /// renderer clamps to H6 at output time.
    /// </summary>
    /// <remarks>
    /// Ports the OpenDataLoader-pdf <c>HeadingProcessor</c> level-assignment
    /// pass with two extensions: indent and alignment join the style key
    /// (so a document whose chapter/section/sub-section headings share font
    /// and weight but sit at distinct left margins can still cluster into
    /// distinct levels), and headings that arrive with a non-zero
    /// <see cref="Models.HeadingElement.HeadingLevel"/> are treated as
    /// already-authoritative and left unchanged. The latter preserves the
    /// hierarchy a pattern-driven classifier (e.g.
    /// <see cref="Analysis.RegexHeadingClassifier"/>) intentionally
    /// assigned per pattern.
    /// </remarks>
    private static void AssignHeadingLevels(
        List<Models.ContentElement> kids,
        IReadOnlyDictionary<int, double> pageWidths)
    {
        var unassigned = kids
            .OfType<Models.HeadingElement>()
            .Where(h => h.HeadingLevel == 0)
            .ToList();
        if (unassigned.Count == 0) return;

        var styleGroups = unassigned
            .GroupBy(h => BuildStyleKey(h, pageWidths))
            .OrderByDescending(g => g.Key.FontSize)
            .ThenByDescending(g => g.Key.IsBold)
            .ThenBy(g => g.Key.AlignmentRank)
            .ThenBy(g => g.Key.IndentBucket)
            .ThenBy(g => g.Key.FontName, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < styleGroups.Count; i++)
        {
            var level = i + 1;
            var label = HeadingLevelLabel(level);
            foreach (var heading in styleGroups[i])
            {
                heading.HeadingLevel = level;
                heading.Level = label;
            }
        }
    }

    /// <summary>
    /// Builds the composite style key used to group headings in
    /// <see cref="AssignHeadingLevels"/>. Font size, bold, and font name
    /// remain the primary axes; indent (rounded to a 5pt bucket) and
    /// alignment (centered vs left-aligned) are added so headings that
    /// share typography but differ in layout role end up in distinct
    /// groups — a sub-section indented one column further than its
    /// parent chapter, for example, or a centred document title above
    /// left-aligned section headings of the same font size.
    /// </summary>
    private static TextStyleKey BuildStyleKey(Models.HeadingElement heading, IReadOnlyDictionary<int, double> pageWidths)
    {
        var indentBucket = (int)Math.Round(heading.BoundingBox.Left / 5.0);
        var alignmentRank = ClassifyAlignment(heading, pageWidths);
        return new TextStyleKey(
            FontSize: heading.Text.FontSize,
            IsBold: IsBoldFontName(heading.Text.Font),
            FontName: heading.Text.Font,
            IndentBucket: indentBucket,
            AlignmentRank: alignmentRank);
    }

    /// <summary>
    /// Maps a heading's horizontal position on its page to a coarse rank:
    /// <c>0</c> for centred (both side margins substantial and roughly
    /// equal), <c>1</c> for left-aligned, <c>2</c> when page geometry is
    /// unknown. The rank doubles as the within-cluster sort order, so a
    /// centred title naturally precedes a left-aligned heading of the
    /// same font size when both groups need to be ranked.
    /// </summary>
    private static int ClassifyAlignment(
        Models.HeadingElement heading,
        IReadOnlyDictionary<int, double> pageWidths)
    {
        if (!pageWidths.TryGetValue(heading.PageNumber, out var pageWidth) || pageWidth <= 0)
            return 2;

        var leftMargin = heading.BoundingBox.Left;
        var rightMargin = pageWidth - heading.BoundingBox.Right;
        if (leftMargin <= 0 || rightMargin <= 0) return 1;

        var minMargin = pageWidth * 0.15;
        if (leftMargin < minMargin || rightMargin < minMargin) return 1;

        var asymmetry = Math.Abs(leftMargin - rightMargin) / pageWidth;
        return asymmetry < 0.05 ? 0 : 1;
    }

    /// <summary>Composite typographic-and-layout style key used for grouping headings.</summary>
    private readonly record struct TextStyleKey(double FontSize, bool IsBold, string FontName, int IndentBucket, int AlignmentRank);

    /// <summary>
    /// Heuristic bold detection from a font name. Mirrors the
    /// <see cref="TextLineBuilder.IsBold"/> derivation but is repeated here because
    /// only the rendered <see cref="Models.TextProperties.Font"/> string is
    /// preserved on the heading element.
    /// </summary>
    private static bool IsBoldFontName(string fontName) =>
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Black", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the structural label for a heading level. Mirrors OpenDataLoader-pdf's
    /// vocabulary: <c>Doctitle</c> for the document title (level 1) and <c>Subtitle</c>
    /// for every nested heading (level 2 and below). The numeric depth is carried by
    /// <see cref="Models.HeadingElement.HeadingLevel"/>; this string is the coarse
    /// semantic tag, not a depth encoding.
    /// </summary>
    private static string HeadingLevelLabel(int level) => level == 1 ? "Doctitle" : "Subtitle";

    /// <summary>
    /// Renumbers elements sequentially while preserving the order produced by
    /// the extraction and layout-analysis pipeline. Top-level elements are
    /// numbered 1..N first; nested children inside list items are then
    /// numbered with the next available identifier so every element in the
    /// document has a unique id.
    /// </summary>
    private static void RenumberElements(List<Models.ContentElement> elements)
    {
        var nextId = 1;
        foreach (var element in elements)
            element.Id = nextId++;

        foreach (var element in elements)
        {
            if (element is not Models.ListElement list) continue;
            foreach (var item in list.ListItems)
                foreach (var child in item.Kids)
                    child.Id = nextId++;
        }
    }

    /// <summary>
    /// Sentinel-text prefix used to inject list placeholders into the
    /// classifier-bound text-block stream. The placeholder is replaced with
    /// the actual <see cref="Models.ListElement"/> after classification.
    /// </summary>
    private const string ListPlaceholderPrefix = "PDFSTRUCT_LIST_PLACEHOLDER";

    /// <summary>
    /// Runs the Phase 1 list detector against each page's residual line
    /// stream, mutates the line stream to remove claimed lines, and
    /// returns the per-page detected list runs. Pages with no detected
    /// lists are absent from the returned dictionary.
    /// </summary>
    private static Dictionary<int, IReadOnlyList<DetectedList>> DetectListsPerPage(
        Dictionary<int, IReadOnlyList<TextLineBlock>> pageLines)
    {
        var result = new Dictionary<int, IReadOnlyList<DetectedList>>();
        foreach (var page in pageLines.Keys.ToList())
        {
            var detection = ListDetector.Detect(pageLines[page]);
            if (detection.Lists.Count == 0) continue;

            result[page] = detection.Lists;
            pageLines[page] = detection.ResidualLines;
        }
        return result;
    }

    /// <summary>
    /// Builds a <see cref="TextBlock"/> placeholder representing a detected
    /// list. The placeholder participates in the layout-analysis reading
    /// order alongside paragraph blocks; after classification it is
    /// recognised by its sentinel text and replaced with a real
    /// <see cref="Models.ListElement"/>.
    /// </summary>
    /// <summary>
    /// Enforces the structural invariants required of detector output by
    /// dropping any list whose bounding box overlaps with another list's
    /// bounding box, or whose bounding box substantially contains a
    /// provisional paragraph block. Lines claimed by dropped lists are
    /// returned to the page's residual line stream so they participate in
    /// the final paragraph merge.
    /// </summary>
    /// <remarks>
    /// "False negative is preferable to false positive": a list that
    /// cannot cleanly own its territory is not emitted at all. Phase 1 has
    /// no rescue mechanism that could absorb intervening content into a
    /// list's children, so the only safe response to a violation is to
    /// un-confirm the offending list.
    /// </remarks>
    private static void ApplyConservativeReconciliation(
        Dictionary<int, IReadOnlyList<TextLineBlock>> pageResidualLines,
        IReadOnlyDictionary<int, IReadOnlyList<TextLineBlock>> originalPageLines,
        Dictionary<int, IReadOnlyList<DetectedList>> pageLists)
    {
        foreach (var page in pageLists.Keys.ToList())
        {
            var lists = pageLists[page];
            var residualBlocks = MergeLinesIntoBlocks(pageResidualLines[page]);
            var rejected = IdentifyInvariantViolators(lists, residualBlocks);
            if (rejected.Count == 0) continue;

            var kept = lists.Where(list => !rejected.Contains(list)).ToList();
            var keptClaimed = new HashSet<int>();
            foreach (var list in kept)
                foreach (var item in list.Items)
                    foreach (var idx in item.ClaimedLineIndices)
                        keptClaimed.Add(idx);

            pageResidualLines[page] = originalPageLines[page]
                .Where((_, index) => !keptClaimed.Contains(index))
                .ToList();

            if (kept.Count == 0)
                pageLists.Remove(page);
            else
                pageLists[page] = kept;
        }
    }

    /// <summary>
    /// Identifies which confirmed lists violate the structural invariants
    /// of detector output: pairwise sibling-list bounding-box disjointness,
    /// and the absence of any provisional paragraph block substantially
    /// contained within a list's bounding box.
    /// </summary>
    private static HashSet<DetectedList> IdentifyInvariantViolators(
        IReadOnlyList<DetectedList> lists,
        IReadOnlyList<TextBlock> provisionalParagraphs)
    {
        var rejected = new HashSet<DetectedList>();

        for (var i = 0; i < lists.Count; i++)
        {
            for (var j = i + 1; j < lists.Count; j++)
            {
                if (lists[i].BoundingBox.Overlaps(lists[j].BoundingBox))
                {
                    rejected.Add(lists[i]);
                    rejected.Add(lists[j]);
                }
            }
        }

        foreach (var list in lists)
        {
            if (rejected.Contains(list)) continue;
            foreach (var paragraph in provisionalParagraphs)
            {
                if (BoundingBoxSubstantiallyContains(list.BoundingBox, paragraph.BoundingBox))
                {
                    rejected.Add(list);
                    break;
                }
            }
        }

        return rejected;
    }

    /// <summary>
    /// Returns <c>true</c> when at least 80% of <paramref name="inner"/>'s
    /// area falls inside <paramref name="container"/>. Used by the
    /// reconciliation pass to detect paragraphs that share a list's
    /// bounding-box interior.
    /// </summary>
    private static bool BoundingBoxSubstantiallyContains(Models.BoundingBox container, Models.BoundingBox inner)
    {
        var overlapLeft = Math.Max(container.Left, inner.Left);
        var overlapRight = Math.Min(container.Right, inner.Right);
        var overlapBottom = Math.Max(container.Bottom, inner.Bottom);
        var overlapTop = Math.Min(container.Top, inner.Top);
        if (overlapRight <= overlapLeft || overlapTop <= overlapBottom) return false;

        var overlapArea = (overlapRight - overlapLeft) * (overlapTop - overlapBottom);
        var innerArea = inner.Width * inner.Height;
        return innerArea > 0 && overlapArea / innerArea >= 0.8;
    }

    /// <summary>
    /// Synthesises a body-typical <see cref="TextBlock"/> per detected list
    /// item, used only as input to <see cref="DocumentStatistics"/>. The
    /// placeholder block (different concept, see
    /// <see cref="MakeListPlaceholder"/>) is what flows through the
    /// classifier; this stats block exists to keep document-wide font
    /// statistics close to the pre-detection distribution so that paragraph
    /// vs heading classification on unrelated blocks is not destabilised
    /// by the act of removing list lines from the paragraph merge.
    /// </summary>
    private static TextBlock SynthesizeListItemStatsBlock(DetectedList list, DetectedListItem item) => new(
        item.BoundingBox,
        item.Body,
        list.FontName,
        list.FontSize,
        IsBold: false,
        LineCount: 1,
        FirstLineLeft: item.BoundingBox.Left,
        MedianLineLeft: item.BoundingBox.Left,
        LastLineLeft: item.BoundingBox.Left,
        FirstLineRight: item.BoundingBox.Right,
        MedianLineRight: item.BoundingBox.Right,
        LastLineRight: item.BoundingBox.Right);

    /// <summary>
    /// Builds a sentinel <see cref="TextBlock"/> standing in for a detected
    /// list during reading-order analysis. The marker text encodes the
    /// page-and-index pair so <see cref="ReplaceListPlaceholders"/> can
    /// resolve it back to the corresponding <see cref="Models.ListElement"/>
    /// after classification has run.
    /// </summary>
    private static TextBlock MakeListPlaceholder(DetectedList list, int pageNumber, int indexOnPage)
    {
        var marker = $"{ListPlaceholderPrefix}{pageNumber}{indexOnPage}";
        return new TextBlock(
            list.BoundingBox,
            marker,
            list.FontName,
            list.FontSize,
            IsBold: false,
            LineCount: list.Items.Count,
            FirstLineLeft: list.BoundingBox.Left,
            MedianLineLeft: list.BoundingBox.Left,
            LastLineLeft: list.BoundingBox.Left,
            FirstLineRight: list.BoundingBox.Right,
            MedianLineRight: list.BoundingBox.Right,
            LastLineRight: list.BoundingBox.Right) with
        { IsStandalone = false };
    }

    /// <summary>
    /// Walks the document's element list and replaces every placeholder
    /// element (recognised by sentinel text content) with the
    /// corresponding <see cref="Models.ListElement"/>. Both
    /// <see cref="Models.ParagraphElement"/> and
    /// <see cref="Models.HeadingElement"/> are handled because the
    /// classifier may resolve the sentinel block into either type.
    /// </summary>
    private static void ReplaceListPlaceholders(
        List<Models.ContentElement> kids,
        Dictionary<int, IReadOnlyList<DetectedList>> pageLists,
        IReadOnlyDictionary<int, IReadOnlyList<TextLineBlock>> originalPageLines)
    {
        for (var i = 0; i < kids.Count; i++)
        {
            var element = kids[i];
            var content = element switch
            {
                Models.ParagraphElement p => p.Text.Content,
                Models.HeadingElement h => h.Text.Content,
                _ => null
            };
            if (content is null) continue;
            if (!content.StartsWith(ListPlaceholderPrefix, StringComparison.Ordinal)) continue;

            var rest = content[ListPlaceholderPrefix.Length..];
            var sep = rest.IndexOf('');
            if (sep < 0) continue;
            if (!int.TryParse(rest.AsSpan(0, sep), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pageNumber)) continue;
            if (!int.TryParse(rest.AsSpan(sep + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var indexOnPage)) continue;
            if (!pageLists.TryGetValue(pageNumber, out var lists)) continue;
            if (indexOnPage < 0 || indexOnPage >= lists.Count) continue;
            if (!originalPageLines.TryGetValue(pageNumber, out var pageLines)) continue;

            kids[i] = BuildListElement(lists[indexOnPage], pageNumber, element.Id, pageLines);
        }
    }

    /// <summary>
    /// Materialises a <see cref="Models.ListElement"/> from a detector
    /// output. The numbering style is fixed at <c>"ordered"</c> for Phase 1
    /// and Phase 2 (Arabic-numeric labels only). Per-item children, if any
    /// were absorbed by the territory walk (Phase 2 § 6), are formed here
    /// by feeding each item's child line indices through the same
    /// paragraph-merger function the page-level pipeline uses, then
    /// wrapping each resulting block as a paragraph element child of the
    /// list item. Child element identifiers are assigned later by
    /// <see cref="RenumberElements"/>.
    /// </summary>
    private static Models.ListElement BuildListElement(
        DetectedList list,
        int pageNumber,
        int id,
        IReadOnlyList<TextLineBlock> pageLines)
    {
        var element = new Models.ListElement
        {
            Id = id,
            PageNumber = pageNumber,
            BoundingBox = list.BoundingBox,
            NumberingStyle = "ordered",
            NumberOfListItems = list.Items.Count
        };
        foreach (var item in list.Items)
        {
            var listItem = new Models.ListItem
            {
                BoundingBox = item.BoundingBox,
                PageNumber = pageNumber,
                Text = new Models.TextProperties
                {
                    Content = item.Body,
                    FontSize = list.FontSize,
                    Font = list.FontName
                }
            };

            if (item.ChildrenLineIndices.Count > 0)
            {
                var childLines = item.ChildrenLineIndices
                    .Select(idx => pageLines[idx])
                    .ToList();
                var childBlocks = MergeLinesIntoBlocks(childLines);
                foreach (var block in childBlocks)
                {
                    listItem.Kids.Add(new Models.ParagraphElement
                    {
                        PageNumber = pageNumber,
                        BoundingBox = block.BoundingBox,
                        Text = new Models.TextProperties
                        {
                            Content = block.Text,
                            Font = block.FontName,
                            FontSize = block.FontSize
                        }
                    });
                }
            }

            element.ListItems.Add(listItem);
        }
        return element;
    }

    /// <summary>
    /// Removes lines that look like running headers, footers, or vertical
    /// side furniture from the per-page line stream. Header and footer
    /// candidates are rejected only when their normalised text appears in
    /// the same band-and-x-position bucket on a configurable fraction of
    /// pages, which keeps a centred document title from being dropped
    /// alongside a recurring page header that shares its text. Side
    /// furniture (narrow, tall margin glyph runs) is rejected
    /// unconditionally — a single occurrence is enough.
    /// </summary>
    private static Dictionary<int, IReadOnlyList<TextLineBlock>> FilterRunningFurnitureLines(
        IReadOnlyDictionary<int, IReadOnlyList<TextLineBlock>> pageLines,
        IReadOnlyDictionary<int, PageGeometry> pageGeometries)
    {
        if (pageLines.Count < 3) return pageLines.ToDictionary(pair => pair.Key, pair => pair.Value);

        var candidates = new List<RunningLineCandidate>();
        foreach (var (pageNumber, lines) in pageLines)
        {
            if (!pageGeometries.TryGetValue(pageNumber, out var pageGeometry))
                continue;

            for (var index = 0; index < lines.Count; index++)
            {
                var band = ClassifyRunningFurnitureBand(lines[index].BoundingBox, pageGeometry);
                if (band is null) continue;

                var normalized = NormalizeRunningFurnitureText(lines[index].Text);
                if (string.IsNullOrWhiteSpace(normalized)) continue;

                var quantisedLeft = Math.Round(lines[index].BoundingBox.Left / 10.0) * 10.0;
                candidates.Add(new RunningLineCandidate(pageNumber, index, band.Value, normalized, quantisedLeft));
            }
        }

        var repeatedHeaderFooter = candidates
            .Where(candidate => candidate.Band is not RunningFurnitureBand.Side);

        var minPagesForRepeat = Math.Max(2, (int)Math.Ceiling(pageLines.Count * RunningFurnitureDetector.RepeatRatioThreshold));
        // Group by position too (quantised to 10pt buckets) — two lines that share
        // text content but appear at very different lefts are not the same running
        // element. Without this, a centred document title is removed alongside a
        // recurring page header that happens to share the same text.
        var rejected = repeatedHeaderFooter
            .GroupBy(candidate => (candidate.Band, candidate.NormalizedText, candidate.QuantisedLeft))
            .Where(group => group.Select(candidate => candidate.PageNumber).Distinct().Count() >= minPagesForRepeat)
            .SelectMany(group => group.Select(candidate => (candidate.PageNumber, candidate.LineIndex)))
            .ToHashSet();

        foreach (var candidate in candidates.Where(candidate => candidate.Band is RunningFurnitureBand.Side))
            rejected.Add((candidate.PageNumber, candidate.LineIndex));

        if (rejected.Count == 0)
            return pageLines.ToDictionary(pair => pair.Key, pair => pair.Value);

        return pageLines.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TextLineBlock>)pair.Value
                .Where((_, index) => !rejected.Contains((pair.Key, index)))
                .ToList());
    }

    /// <summary>
    /// Classifies a line's bounding box as belonging to the page's header,
    /// footer, or side-furniture band, or returns <c>null</c> when the box
    /// sits in the body region. Header and footer bands are defined by Y
    /// ratios; side bands require a narrow-and-tall box hugging the left or
    /// right page edge.
    /// </summary>
    private static RunningFurnitureBand? ClassifyRunningFurnitureBand(Models.BoundingBox bbox, PageGeometry pageGeometry)
    {
        if (pageGeometry.Height <= 0 || pageGeometry.Width <= 0)
            return null;

        var bottomRatio = bbox.Bottom / pageGeometry.Height;
        var topRatio = bbox.Top / pageGeometry.Height;
        if (topRatio < RunningFurnitureDetector.FooterBandBottomRatio)
            return RunningFurnitureBand.Footer;
        if (bottomRatio > 1.0 - RunningFurnitureDetector.HeaderBandTopRatio)
            return RunningFurnitureBand.Header;

        var nearLeftOrRightEdge = bbox.Right <= pageGeometry.Width * 0.12
            || bbox.Left >= pageGeometry.Width * 0.88;
        var narrowAndTall = bbox.Width <= SideFurnitureMaxWidth(pageGeometry)
            && bbox.Height >= pageGeometry.Height * 0.15;
        return nearLeftOrRightEdge && narrowAndTall
            ? RunningFurnitureBand.Side
            : null;
    }

    /// <summary>
    /// Returns <c>true</c> when a block's bounding box looks like vertical
    /// side furniture: hugging a page edge, narrower than the side-furniture
    /// width allowance, and tall enough relative to the page height.
    /// Used after block formation to reject merged side-furniture columns
    /// that the line-level filter alone would have missed.
    /// </summary>
    private static bool IsSideFurnitureBlock(Models.BoundingBox bbox, PageGeometry pageGeometry)
    {
        if (pageGeometry.Height <= 0 || pageGeometry.Width <= 0)
            return false;

        var nearLeftOrRightEdge = bbox.Right <= pageGeometry.Width * 0.12
            || bbox.Left >= pageGeometry.Width * 0.88;
        return nearLeftOrRightEdge
            && bbox.Width <= SideFurnitureMaxWidth(pageGeometry)
            && bbox.Height >= pageGeometry.Height * 0.08;
    }

    /// <summary>
    /// Returns the maximum width, in PDF points, allowed for a block to
    /// qualify as side furniture. Scales modestly with page width but is
    /// clamped to a 16–28pt band so an unusually wide page does not invite
    /// false positives in the body column.
    /// </summary>
    private static double SideFurnitureMaxWidth(PageGeometry pageGeometry) =>
        Math.Max(16.0, Math.Min(28.0, pageGeometry.Width * 0.08));

    /// <summary>
    /// Normalises a candidate header/footer line for repeat-detection
    /// matching by collapsing digit runs to <c>#</c> and folding internal
    /// whitespace to a single space. Page numbers and date stamps that
    /// vary across pages still match each other after the digit-run mask.
    /// </summary>
    private static string NormalizeRunningFurnitureText(string text)
    {
        var normalized = s_digitRun.Replace(text.Trim(), "#");
        normalized = s_whitespace.Replace(normalized, " ");
        return normalized;
    }

    /// <summary>
    /// Converts a PDF date string in the form
    /// <c>D:YYYYMMDDHHmmSS[+|-]HH'mm'</c> (or its truncated variants) to an
    /// ISO 8601 representation like <c>2026-04-30T11:30:09+09:00</c>. Returns
    /// the input unchanged if it cannot be parsed.
    /// </summary>
    /// <remarks>
    /// PdfPig surfaces the PDF date dictionary as the raw string PDF stores
    /// it. Emitting that string directly into the OpenDataLoader-compatible
    /// JSON makes the field hostile to anyone reading it; ISO 8601 keeps
    /// the field usable while preserving the documented JSON shape.
    /// </remarks>
    private static string? NormalizePdfDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var s = raw.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];

        var match = System.Text.RegularExpressions.Regex.Match(
            s,
            @"^(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:([+\-Z])(\d{2})?(?:'(\d{2})'?)?)?$");
        if (!match.Success) return raw;

        try
        {
            var year = int.Parse(match.Groups[1].Value);
            var month = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
            var day = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 1;
            var hour = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
            var minute = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : 0;
            var second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value) : 0;

            TimeSpan offset;
            if (!match.Groups[7].Success || match.Groups[7].Value == "Z")
            {
                offset = TimeSpan.Zero;
            }
            else
            {
                var sign = match.Groups[7].Value == "+" ? 1 : -1;
                var offsetHours = match.Groups[8].Success ? int.Parse(match.Groups[8].Value) : 0;
                var offsetMinutes = match.Groups[9].Success ? int.Parse(match.Groups[9].Value) : 0;
                offset = new TimeSpan(sign * offsetHours, sign * offsetMinutes, 0);
            }

            var dto = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            return dto.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>
    /// Extracts ordered text blocks for a single page: word grouping, hidden-text
    /// filtering, sanitation, reading-order analysis, and standalone-flag
    /// computation. Classification is performed later, after document-wide
    /// statistics are available.
    /// </summary>
    private IReadOnlyList<TextBlock> ExtractPageBlocks(Page page)
    {
        var lines = ExtractPageTextLines(page);
        var gutters = DetectColumnGutters(page, _options.FilterHiddenText);
        PageGeometry? pageGeometry = _options.ExcludeHeadersFooters ? new PageGeometry(page.Width, page.Height) : null;
        return BuildPageBlocks(lines, pageGeometry, gutters);
    }

    private IReadOnlyList<TextBlock> BuildPageBlocks(
        IReadOnlyList<TextLineBlock> lines,
        PageGeometry? pageGeometry = null,
        IReadOnlyList<PdfRectangle>? columnGutters = null)
    {
        if (lines.Count == 0) return [];

        var textBlocks = columnGutters is { Count: > 0 }
            ? BuildPageBlocksColumnAware(lines, columnGutters)
            : BuildPageBlocksSingleColumn(lines);

        if (pageGeometry is { } geometry)
        {
            textBlocks = textBlocks
                .Where(block => !IsSideFurnitureBlock(block.BoundingBox, geometry))
                .ToList();
            if (textBlocks.Count == 0) return [];
        }

        var ordered = _layoutAnalyzer.DetermineReadingOrder(textBlocks);
        return WithStandaloneFlag(ordered);
    }

    /// <summary>
    /// Single-column path: existing reading-order + line-merge pipeline. Used
    /// for pages where <see cref="DetectColumnGutters"/> finds no structural
    /// vertical gutter.
    /// </summary>
    private List<TextBlock> BuildPageBlocksSingleColumn(IReadOnlyList<TextLineBlock> lines)
    {
        var orderedLines = DetermineTextLineReadingOrder(lines);
        return MergeLinesIntoBlocks(orderedLines);
    }

    /// <summary>
    /// Column-aware path: lines whose vertical centre falls inside the
    /// gutter band are partitioned by column (assigned by horizontal centre
    /// against the gutter X-centres) and merged column-locally; lines above
    /// or below the band fall back to the single-column path so headings
    /// or footers that span columns are not sliced.
    /// </summary>
    private List<TextBlock> BuildPageBlocksColumnAware(
        IReadOnlyList<TextLineBlock> lines,
        IReadOnlyList<PdfRectangle> columnGutters)
    {
        var bandTop = columnGutters.Max(g => g.Top);
        var bandBottom = columnGutters.Min(g => g.Bottom);
        var boundaries = columnGutters
            .Select(g => (g.Left + g.Right) / 2.0)
            .OrderBy(c => c)
            .ToList();

        var aboveBand = new List<TextLineBlock>();
        var belowBand = new List<TextLineBlock>();
        var columns = new List<List<TextLineBlock>>(boundaries.Count + 1);
        for (var i = 0; i < boundaries.Count + 1; i++)
            columns.Add(new List<TextLineBlock>());

        foreach (var line in lines)
        {
            var lineCenterY = (line.Top + line.Bottom) / 2.0;
            if (lineCenterY > bandTop)
                aboveBand.Add(line);
            else if (lineCenterY < bandBottom)
                belowBand.Add(line);
            else
                columns[ColumnIndex(line, boundaries)].Add(line);
        }

        var result = new List<TextBlock>();

        if (aboveBand.Count > 0)
            result.AddRange(BuildPageBlocksSingleColumn(aboveBand));

        for (var c = 0; c < columns.Count; c++)
        {
            if (columns[c].Count == 0) continue;
            var orderedColumn = columns[c].OrderByDescending(l => l.Top).ToList();
            result.AddRange(MergeLinesIntoBlocks(orderedColumn));
        }

        if (belowBand.Count > 0)
            result.AddRange(BuildPageBlocksSingleColumn(belowBand));

        return result;
    }

    /// <summary>
    /// Returns the index of the column slab that <paramref name="line"/>
    /// belongs to, by binary placement of the line's horizontal centre
    /// against the gutter <paramref name="boundaries"/>. Slab count is
    /// <c>boundaries.Count + 1</c>.
    /// </summary>
    private static int ColumnIndex(TextLineBlock line, IReadOnlyList<double> boundaries)
    {
        var center = (line.Left + line.Right) / 2.0;
        for (var i = 0; i < boundaries.Count; i++)
        {
            if (center < boundaries[i]) return i;
        }
        return boundaries.Count;
    }

    /// <summary>
    /// Detects structural vertical gutters on a page using PdfPig's
    /// <see cref="PdfPigWhitespaceCover"/>. A gutter must span at least
    /// 50% of the page height and be no wider than 10% of the page width;
    /// returned rectangles drive column-aware line partitioning in
    /// <see cref="BuildPageBlocksColumnAware"/>. Pages with sparse content,
    /// no qualifying gutter, or single-column layouts get an empty list and
    /// fall through to the legacy page-wide pipeline.
    /// </summary>
    /// <remarks>
    /// Two near-coincident gutters within 5pt of each other on the X-axis
    /// are deduplicated to a single boundary; gutter pairs that flank a
    /// narrow strip of decoration text (the central line-number column on
    /// US patent body pages, for example) sit ~30pt apart and are kept
    /// separately, producing the three-column slab layout the strip
    /// requires.
    /// </remarks>
    private static IReadOnlyList<PdfRectangle> DetectColumnGutters(Page page, bool filterHiddenText)
    {
        IReadOnlyList<Letter> letters;
        if (filterHiddenText)
        {
            var visibleLetters = page.Letters.Where(IsVisibleLetter).ToList();
            letters = visibleLetters.Count > 0 ? visibleLetters : page.Letters;
        }
        else
        {
            letters = page.Letters;
        }

        if (letters.Count == 0) return [];

        var words = LetterGrouper.Instance.GetWords(letters).ToList();
        if (words.Count < 10) return [];

        var whitespaces = PdfPigWhitespaceCover.GetWhitespaces(
            words,
            images: null,
            maxRectangleCount: 200);

        var minGutterHeight = page.Height * 0.5;
        var maxGutterWidth = page.Width * 0.1;

        var gutters = whitespaces
            .Where(ws => ws.Width > 0
                         && ws.Height >= minGutterHeight
                         && ws.Width <= maxGutterWidth)
            .OrderBy(ws => (ws.Left + ws.Right) / 2.0)
            .ToList();

        if (gutters.Count == 0) return [];

        var deduplicated = new List<PdfRectangle>();
        var lastCenter = double.NegativeInfinity;
        const double minSeparation = 5.0;
        foreach (var g in gutters)
        {
            var center = (g.Left + g.Right) / 2.0;
            if (center - lastCenter < minSeparation) continue;
            deduplicated.Add(g);
            lastCenter = center;
        }

        return deduplicated;
    }

    private IReadOnlyList<TextLineBlock> DetermineTextLineReadingOrder(IReadOnlyList<TextLineBlock> lines)
    {
        if (lines.Count <= 1) return lines;

        var lineBlocks = lines.Select(line => line.ToTextBlock()).ToList();
        var lineByBlock = new Dictionary<TextBlock, TextLineBlock>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < lineBlocks.Count; i++)
            lineByBlock[lineBlocks[i]] = lines[i];

        return _layoutAnalyzer.DetermineReadingOrder(lineBlocks)
            .Select(block => lineByBlock[block])
            .ToList();
    }

    /// <summary>
    /// Extracts text lines for a page before paragraph merging. This keeps the
    /// word-to-line stage separate from the later line-to-block stage.
    /// </summary>
    private IReadOnlyList<TextLineBlock> ExtractPageTextLines(Page page)
    {
        // The "Neither" / "NeitherClip" rendering modes (Tr 3 / Tr 7) are the
        // PDF spec's way of saying "do not draw this glyph". Some scanned
        // PDFs use them for an OCR text layer that duplicates the visible
        // glyphs and distorts font and gap statistics — we want those
        // dropped. But other generators (notably some patent-office
        // pipelines) tag legitimate, visually rendered text with the same
        // mode. Use a per-page guard: drop the never-drawn glyphs only when
        // the page also carries visibly drawn ones, so a page whose entire
        // letter stream reports never-drawn keeps its content rather than
        // disappearing.
        IReadOnlyList<Letter> letters;
        if (_options.FilterHiddenText)
        {
            var visibleLetters = page.Letters.Where(IsVisibleLetter).ToList();
            letters = visibleLetters.Count > 0 ? visibleLetters : page.Letters;
        }
        else
        {
            letters = page.Letters;
        }

        var words = letters.Count > 0
            ? LetterGrouper.Instance.GetWords(letters).ToList()
            : [];
        if (words.Count == 0) return [];

        var lines = GroupWordsIntoLines(words);
        if (_options.FilterHiddenText)
            lines = FilterTextLines(lines, page.Width, page.Height);

        return ProcessTextLines(lines);
    }

    /// <summary>
    /// Returns <c>true</c> for letters whose rendering mode draws to the
    /// page (fill, stroke, or both, with or without clipping). Returns
    /// <c>false</c> for the <c>Neither</c> and <c>NeitherClip</c> modes,
    /// which are typically the invisible OCR text layer in scanned PDFs.
    /// </summary>
    private static bool IsVisibleLetter(Letter letter) =>
        letter.RenderingMode != TextRenderingMode.Neither
        && letter.RenderingMode != TextRenderingMode.NeitherClip;

    /// <summary>
    /// Returns a copy of <paramref name="blocks"/> with each block's
    /// <see cref="TextBlock.IsStandalone"/> flag set based on whether any
    /// other block on the page overlaps its vertical row by more than 50%.
    /// </summary>
    private static IReadOnlyList<TextBlock> WithStandaloneFlag(IReadOnlyList<TextBlock> blocks)
    {
        var result = new List<TextBlock>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var standalone = true;
            for (var j = 0; j < blocks.Count; j++)
            {
                if (i == j) continue;
                if (VerticalOverlapRatio(blocks[i].BoundingBox, blocks[j].BoundingBox) > 0.5)
                {
                    standalone = false;
                    break;
                }
            }
            result.Add(blocks[i] with { IsStandalone = standalone });
        }
        return result;
    }

    /// <summary>
    /// Returns the fraction of <paramref name="a"/>'s vertical span that
    /// overlaps <paramref name="b"/>'s vertical span. Range <c>[0, 1]</c>.
    /// </summary>
    private static double VerticalOverlapRatio(Models.BoundingBox a, Models.BoundingBox b)
    {
        var top = Math.Min(a.Top, b.Top);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        var overlap = Math.Max(0, top - bottom);
        return a.Height > 0 ? overlap / a.Height : 0;
    }

    /// <summary>
    /// Runs each line's text through <see cref="TextSanitizer"/>, returning a
    /// new line list whose text has been redacted, replaced, or normalised
    /// according to the parser's options. Geometry, font, and layout fields
    /// are preserved.
    /// </summary>
    private List<TextLineBlock> ProcessTextLines(IReadOnlyList<TextLineBlock> lines)
    {
        var lineBlocks = lines.Select(l => l.ToTextBlock()).ToList();
        var processed = TextSanitizer.ProcessBlocks(
            lineBlocks,
            _options.SanitizeText,
            _options.InvalidCharacterReplacement,
            _options.SanitizationRules);

        var result = new List<TextLineBlock>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            result.Add(lines[i] with { Text = processed[i].Text });

        return result;
    }

    /// <summary>
    /// Drops lines whose font size is below 1pt, whose visible text is empty,
    /// or whose bounding box falls outside the page rectangle. The font-size
    /// floor is the most useful guard against degenerate trailing-glyph
    /// fragments; the off-page check rejects lines whose ink ended up
    /// outside the visible crop box.
    /// </summary>
    private static List<TextLineBlock> FilterTextLines(
        IReadOnlyList<TextLineBlock> lines,
        double pageWidth,
        double pageHeight)
    {
        return lines.Where(line =>
        {
            if (line.FontSize < 1.0) return false;
            if (string.IsNullOrWhiteSpace(line.Text)) return false;
            if (line.BoundingBox.Right < 0 || line.BoundingBox.Left > pageWidth) return false;
            if (line.BoundingBox.Top < 0 || line.BoundingBox.Bottom > pageHeight) return false;
            return true;
        }).ToList();
    }

    /// <summary>
    /// Groups a page's words into baseline-aligned text lines using a two-phase
    /// relative-threshold strategy.
    /// </summary>
    /// <remarks>
    /// Phase 1 clusters words by typesetting baseline (PdfPig's
    /// <c>Letter.StartBaseLine.Y</c>) and font-size similarity. Two words
    /// belong to the same raw line when their baselines fall within ~30% of
    /// the smaller font's point size and their point sizes differ by less
    /// than ~50%. The smaller-font-relative tolerance is what cleanly
    /// separates a 10pt body row from a 13pt heading row whose baselines
    /// are only a few points apart — using the larger font's height (or
    /// the bbox height of either word) for tolerance lets body and heading
    /// merge incorrectly on densely packed cover-page layouts.
    ///
    /// <para>
    /// Phase 2 walks each raw line left-to-right and splits at any gap that
    /// exceeds <c>min(max(medianGap × 3, avgHeight × 2), 100pt)</c>. The
    /// <c>max</c> combiner of the two relative arms gives the threshold a
    /// glyph-scaled floor so that tightly-kerned small text — where
    /// <c>medianGap × 3</c> alone would land near 6pt — does not
    /// misinterpret a 9pt list-marker indent as a column boundary. The
    /// 100pt absolute cap still cuts sparse rows whose font size pushes
    /// both relative arms past any plausible same-line gap (large
    /// page-number badges in a magazine table of contents).
    /// </para>
    ///
    /// <para>
    /// Earlier revisions used <c>BoundingBox.Bottom</c> for the line-cluster
    /// signal and <c>currentMaxHeight × 0.5</c> for the tolerance. That
    /// pair drifts on glyph descender variation and grows the tolerance as
    /// the larger of two mixed-font words, which is what merged a Korean
    /// patent's 13pt office name with the 10pt publication-date row sitting
    /// 3.6pt below it.
    /// </para>
    /// </remarks>
    private static List<TextLineBlock> GroupWordsIntoLines(List<Word> words)
    {
        if (words.Count == 0) return [];

        var sorted = words
            .OrderByDescending(WordBaselineY)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var rawLines = new List<List<Word>>();
        var currentRawLine = new List<Word> { sorted[0] };
        var currentBaselineY = WordBaselineY(sorted[0]);
        var currentMinFontSize = WordFontSize(sorted[0]);
        var currentMaxFontSize = currentMinFontSize;

        for (int i = 1; i < sorted.Count; i++)
        {
            var w = sorted[i];
            var wBaselineY = WordBaselineY(w);
            var baselineDiff = Math.Abs(wBaselineY - currentBaselineY);
            var wFontSize = WordFontSize(w);

            var maxFontSize = Math.Max(currentMaxFontSize, wFontSize);
            var fontSizeDiffRatio = maxFontSize > 0
                ? Math.Abs(currentMaxFontSize - wFontSize) / maxFontSize
                : 0.0;

            var smallerFontSize = currentMinFontSize > 0 && wFontSize > 0
                ? Math.Min(currentMinFontSize, wFontSize)
                : Math.Max(currentMinFontSize, wFontSize);
            var baselineTolerance = Math.Max(0.5, smallerFontSize * 0.3);
            var sameLine = baselineDiff <= baselineTolerance && fontSizeDiffRatio <= 0.5;

            if (sameLine)
            {
                currentRawLine.Add(w);
                currentMaxFontSize = Math.Max(currentMaxFontSize, wFontSize);
                if (wFontSize > 0)
                    currentMinFontSize = currentMinFontSize > 0
                        ? Math.Min(currentMinFontSize, wFontSize)
                        : wFontSize;
            }
            else
            {
                rawLines.Add(currentRawLine);
                currentRawLine = new List<Word> { w };
                currentBaselineY = wBaselineY;
                currentMinFontSize = wFontSize;
                currentMaxFontSize = wFontSize;
            }
        }
        rawLines.Add(currentRawLine);

        var lines = new List<TextLineBlock>();
        foreach (var rawLine in rawLines)
        {
            var byX = rawLine.OrderBy(w => w.BoundingBox.Left).ToList();
            var splitIndices = FindOutlierGapSplits(byX);
            var start = 0;
            foreach (var splitIndex in splitIndices)
            {
                lines.Add(BuildTextLineBlock(byX, start, splitIndex + 1));
                start = splitIndex + 1;
            }
            lines.Add(BuildTextLineBlock(byX, start, byX.Count));
        }

        return lines;
    }

    /// <summary>
    /// Hard ceiling, in PDF user-space points, on a single intra-line gap.
    /// Any gap above this is treated as a column boundary regardless of
    /// the line's local statistics. The cap exists for sparse single-row
    /// layouts (e.g. magazine table-of-contents pages where two large
    /// page-number badges sit on the same baseline at opposite ends of
    /// the page) — those rows have only one gap, so the median-relative
    /// rule would never split them. 100pt comfortably covers a
    /// 30pt-font letter-spaced heading (typical title-internal gaps stay
    /// under ~50pt) while still cutting page-number badges separated by
    /// hundreds of points.
    /// </summary>
    private const double MaxIntraLineGapPoints = 100.0;

    /// <summary>
    /// Returns the indices in <paramref name="wordsByX"/> after which a column-like
    /// outlier gap appears. The threshold is the larger of three times the
    /// raw-line median gap and twice the line's average glyph height,
    /// capped at the absolute <see cref="MaxIntraLineGapPoints"/> ceiling.
    /// The two relative arms cooperate: <c>median × 3</c> dominates on
    /// large-font rows whose intra-word gaps are healthy multiples of the
    /// glyph height, while <c>avgHeight × 2</c> establishes a glyph-scaled
    /// floor that prevents the median rule from overreacting on
    /// tightly-kerned small text — the canonical example is a numbered
    /// reference list where an 8pt body has ~2pt intra-word gaps and the
    /// "1." → "Author" indent gap is ~9pt; <c>median × 3</c> alone would
    /// flag that indent as a column boundary and split the reference into
    /// "1." and "Author...". The absolute cap still cuts sparse rows whose
    /// font size pushes both relative arms past any plausible same-line
    /// gap (large page-number badges in a magazine table of contents).
    /// </summary>
    private static List<int> FindOutlierGapSplits(IReadOnlyList<Word> wordsByX)
    {
        if (wordsByX.Count <= 1) return [];

        var gaps = new double[wordsByX.Count - 1];
        for (var i = 0; i < gaps.Length; i++)
        {
            gaps[i] = Math.Max(
                0,
                wordsByX[i + 1].BoundingBox.Left - wordsByX[i].BoundingBox.Right);
        }

        var sortedGaps = (double[])gaps.Clone();
        Array.Sort(sortedGaps);
        var medianGap = sortedGaps[sortedGaps.Length / 2];

        var avgHeight = wordsByX.Average(w => Math.Abs(w.BoundingBox.Top - w.BoundingBox.Bottom));
        var threshold = Math.Min(
            Math.Max(medianGap * 3.0, avgHeight * 2.0),
            MaxIntraLineGapPoints);

        var splits = new List<int>();
        for (var i = 0; i < gaps.Length; i++)
        {
            if (gaps[i] > threshold)
            {
                splits.Add(i);
            }
        }
        return splits;
    }

    /// <summary>
    /// Constructs a <see cref="TextLineBlock"/> from a half-open slice
    /// <c>[start, endExclusive)</c> of <paramref name="words"/>, using the
    /// existing <see cref="TextLineBuilder"/> aggregator to keep bounding-box
    /// and font-statistic computation in one place.
    /// </summary>
    private static TextLineBlock BuildTextLineBlock(IReadOnlyList<Word> words, int start, int endExclusive)
    {
        var builder = new TextLineBuilder(words[start]);
        for (var i = start + 1; i < endExclusive; i++)
        {
            builder.Add(words[i]);
        }
        return builder.ToTextLineBlock();
    }

    /// <summary>
    /// Returns the point size of <paramref name="word"/>'s first letter,
    /// or zero when the word carries no letter information. Word font size
    /// is uniform within a <see cref="LetterGrouper"/>-produced word, so
    /// the first-letter sample is sufficient and cheaper than averaging.
    /// </summary>
    private static double WordFontSize(Word word) =>
        word.Letters.Count > 0 ? word.Letters[0].PointSize : 0.0;

    /// <summary>
    /// Returns the typesetting baseline Y of <paramref name="word"/>'s first
    /// letter, or — when the word carries no letter information — falls back
    /// to the lower edge of the bounding box. Using the actual baseline
    /// instead of <see cref="UglyToad.PdfPig.Core.PdfRectangle.Bottom"/>
    /// keeps line clustering stable for glyphs with descenders (the bbox
    /// bottom drops below the baseline by the descender depth) and matches
    /// PDF's typesetting model — every glyph in a line shares one baseline.
    /// </summary>
    private static double WordBaselineY(Word word) =>
        word.Letters.Count > 0
            ? word.Letters[0].StartBaseLine.Y
            : Math.Min(word.BoundingBox.Bottom, word.BoundingBox.Top);

    /// <summary>
    /// Aggregates an ordered sequence of lines into paragraph-level
    /// <see cref="TextBlock"/> records by walking the stream and asking
    /// <see cref="ShouldMergeWithCurrentBlock"/> whether the next line
    /// continues the running block. Each transition starts a new block.
    /// </summary>
    private static List<TextBlock> MergeLinesIntoBlocks(IReadOnlyList<TextLineBlock> lines)
    {
        if (lines.Count == 0) return [];

        var blocks = new List<List<TextLineBlock>>();
        var current = new List<TextLineBlock> { lines[0] };
        for (var i = 1; i < lines.Count; i++)
        {
            var curr = lines[i];
            if (ShouldMergeWithCurrentBlock(current, curr))
            {
                current.Add(curr);
            }
            else
            {
                blocks.Add(current);
                current = [curr];
            }
        }
        blocks.Add(current);

        return blocks.Select(MergeLines).ToList();
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="next"/> should extend the
    /// running block in <paramref name="currentBlock"/>. Requires matching
    /// font size, bold flag, and font face; horizontal overlap; a vertical
    /// gap within the local line-spacing budget (relaxed when the previous
    /// line ends on a continuation cue); and either a same-left, same-right,
    /// or substantial horizontal-overlap alignment with the previous line.
    /// </summary>
    private static bool ShouldMergeWithCurrentBlock(IReadOnlyList<TextLineBlock> currentBlock, TextLineBlock next)
    {
        var previous = currentBlock[^1];
        if (!IsSameFontSize(previous, next))
            return false;

        if (previous.IsBold != next.IsBold)
            return false;

        if (!IsSameFontFace(previous, next))
            return false;

        if (!AreHorizontallyOverlapping(previous, next))
            return false;

        var gap = previous.Bottom - next.Top;
        if (gap < -Math.Max(previous.AvgHeight, next.AvgHeight) * 0.25)
            return false;

        var avgHeight = Math.Max(previous.AvgHeight, next.AvgHeight);
        var continues = SentenceFlow.IsLineContinuation(previous.Text);
        var maxGap = avgHeight * (continues ? 2.2 : 1.35);
        if (gap > maxGap)
            return false;

        var sameLeft = Math.Abs(previous.Left - next.Left) <= Math.Max(6.0, avgHeight * 0.75);
        var sameRight = Math.Abs(previous.Right - next.Right) <= Math.Max(8.0, avgHeight);
        if (!sameLeft && next.Width > previous.Width * 2.5)
            return false;

        var lineOverlap = HorizontalOverlapRatio(previous, next);
        return sameLeft || sameRight || (continues && lineOverlap >= 0.65);
    }

    /// <summary>
    /// Returns <c>true</c> when two lines overlap by at least 35% of the
    /// narrower line's width — the threshold the block merger uses to call
    /// two lines part of the same paragraph column.
    /// </summary>
    private static bool AreHorizontallyOverlapping(TextLineBlock a, TextLineBlock b) =>
        HorizontalOverlapRatio(a, b) >= 0.35;

    /// <summary>
    /// Returns the fraction of the narrower line's width that overlaps the
    /// other line's horizontal span. Range <c>[0, 1]</c>; zero when the
    /// boxes do not overlap.
    /// </summary>
    private static double HorizontalOverlapRatio(TextLineBlock a, TextLineBlock b)
    {
        var overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var minWidth = Math.Min(a.Width, b.Width);
        return minWidth > 0 ? overlap / minWidth : 0;
    }

    /// <summary>
    /// Merges a contiguous list of <see cref="TextLineBlock"/> records into
    /// a single <see cref="TextBlock"/>, joining their text with newlines,
    /// computing the union bounding box, and capturing first/median/last
    /// left and right edges so downstream classifiers can spot indented
    /// or hanging-indented paragraphs without re-scanning the source lines.
    /// </summary>
    private static TextBlock MergeLines(List<TextLineBlock> lines)
    {
        var text = string.Join("\n", lines.Select(l => l.Text));
        var bbox = lines.Select(l => l.BoundingBox).Aggregate((a, b) => a.Merge(b));
        var first = lines[0];
        var last = lines[^1];

        var sortedLefts = lines.Select(l => l.Left).OrderBy(v => v).ToArray();
        var sortedRights = lines.Select(l => l.Right).OrderBy(v => v).ToArray();
        var medianLeft = sortedLefts[sortedLefts.Length / 2];
        var medianRight = sortedRights[sortedRights.Length / 2];

        return new TextBlock(
            bbox,
            text,
            first.FontName,
            first.FontSize,
            first.IsBold,
            LineCount: lines.Count,
            FirstLineLeft: first.Left,
            MedianLineLeft: medianLeft,
            LastLineLeft: last.Left,
            FirstLineRight: first.Right,
            MedianLineRight: medianRight,
            LastLineRight: last.Right,
            IsItalic: first.IsItalic,
            FontWeight: first.FontWeight);
    }

    /// <summary>Returns <c>true</c> when two lines have effectively the same font size (within 10% or 1pt).</summary>
    private static bool IsSameFontSize(TextLineBlock a, TextLineBlock b)
    {
        var delta = Math.Abs(a.FontSize - b.FontSize);
        var tolerance = Math.Max(1.0, 0.1 * Math.Max(a.FontSize, b.FontSize));
        return delta <= tolerance;
    }

    /// <summary>
    /// Returns <c>true</c> when two lines use the same font face. Compares
    /// names with the PDF subset prefix stripped — embedded subset fonts
    /// carry a synthetic six-uppercase-letter tag (for example
    /// <c>INPILL+HCRDotum</c>) that varies between embedding passes and
    /// must be ignored. A bold face and its regular sibling
    /// (<c>HCRDotum-Bold</c> vs <c>HCRDotum</c>) deliberately do <em>not</em>
    /// match — splitting on weight changes is what keeps a bold title
    /// from being merged into the regular-weight body that follows it.
    /// </summary>
    private static bool IsSameFontFace(TextLineBlock a, TextLineBlock b) =>
        StripSubsetPrefix(a.FontName) == StripSubsetPrefix(b.FontName);

    /// <summary>
    /// Strips the six-uppercase-letter subset prefix and trailing <c>+</c>
    /// from a PDF font name, leaving the underlying face identifier. Returns
    /// the input unchanged when no prefix is present.
    /// </summary>
    private static string StripSubsetPrefix(string fontName)
    {
        if (fontName.Length <= 7 || fontName[6] != '+')
            return fontName;

        for (var i = 0; i < 6; i++)
        {
            var c = fontName[i];
            if (c < 'A' || c > 'Z') return fontName;
        }
        return fontName[7..];
    }

    private static readonly Regex s_digitRun = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Width and height of a PDF page in user-space points, used by the running-furniture geometry checks.</summary>
    private readonly record struct PageGeometry(double Width, double Height);

    /// <summary>One line under consideration as a running-furniture candidate, paired with its band classification, normalised text, and quantised left edge for repeat detection.</summary>
    private readonly record struct RunningLineCandidate(
        int PageNumber,
        int LineIndex,
        RunningFurnitureBand Band,
        string NormalizedText,
        double QuantisedLeft);

    /// <summary>Page band that a running-furniture candidate occupies: top header, bottom footer, or vertical side margin.</summary>
    private enum RunningFurnitureBand { Header, Footer, Side }

    /// <summary>
    /// Aggregator that accumulates words sharing one baseline and exposes
    /// the line-level geometric and typographic statistics
    /// <see cref="GroupWordsIntoLines"/> needs to emit a
    /// <see cref="TextLineBlock"/>. Computed properties are evaluated on
    /// demand from the underlying word list.
    /// </summary>
    private sealed class TextLineBuilder
    {
        private readonly List<Word> _words = [];

        /// <summary>Initialises the builder with the first word of the line.</summary>
        public TextLineBuilder(Word w) => _words.Add(w);

        /// <summary>Appends another word to the line under construction.</summary>
        public void Add(Word w) => _words.Add(w);

        /// <summary>Visually-lower Y of the line's first word, used as the line's baseline reference.</summary>
        public double BaselineY => Math.Min(_words[0].BoundingBox.Bottom, _words[0].BoundingBox.Top);

        /// <summary>Minimum left x-coordinate across the line's words.</summary>
        public double Left => _words.Min(w => w.BoundingBox.Left);

        /// <summary>Maximum right x-coordinate across the line's words.</summary>
        public double Right => _words.Max(w => w.BoundingBox.Right);

        // PdfPig's bounding box can have Bottom > Top for rotated text (left-margin
        // arxiv watermarks, vertical sidebars). Normalise with min/max so downstream
        // gap and overlap math stays correct on mixed-orientation pages.

        /// <summary>Visually-lower Y across the line, normalised against rotated-text bbox flipping.</summary>
        public double Bottom => _words.Min(w => Math.Min(w.BoundingBox.Bottom, w.BoundingBox.Top));

        /// <summary>Visually-upper Y across the line, normalised against rotated-text bbox flipping.</summary>
        public double Top => _words.Max(w => Math.Max(w.BoundingBox.Bottom, w.BoundingBox.Top));

        /// <summary>Visible horizontal extent of the line.</summary>
        public double Width => Right - Left;

        /// <summary>Mean glyph height across the line, used as a proxy for typical inter-line spacing.</summary>
        public double AvgHeight => _words.Average(w => Math.Abs(w.BoundingBox.Top - w.BoundingBox.Bottom));

        /// <summary>Mean point size across the line's first letters, defaulted to 12pt when font information is missing.</summary>
        public double AvgFontSize => _words.Average(w => w.Letters.FirstOrDefault()?.PointSize ?? 12.0);

        /// <summary>
        /// First word's font name with the PDF subset prefix stripped.
        /// Embedded subset fonts carry a six-uppercase-letter tag prefix
        /// (<c>BFSYCV+Garamond</c>) that is regenerated each embedding pass —
        /// two passes of the same source font yield different prefixes.
        /// Stripping at the source keeps downstream callers, the JSON
        /// renderer, font-face comparisons, and the heading style-key
        /// clustering all looking at the stable family identifier.
        /// </summary>
        public string FontName => StripSubsetPrefix(_words[0].Letters.FirstOrDefault()?.FontName ?? "");

        /// <summary>
        /// First word's parsed font descriptor, or <c>null</c> when no
        /// letter information is available. Authoritative typographic
        /// flags come from this descriptor; the substring fallbacks below
        /// only fire when it is missing, which happens for synthetic glyph
        /// streams without an embedded font descriptor.
        /// </summary>
        private UglyToad.PdfPig.PdfFonts.FontDetails? FirstFontDetails =>
            _words[0].Letters.FirstOrDefault()?.FontDetails;

        /// <summary>
        /// Whether the line's font signals bold weight. Reads
        /// <c>FontDetails.IsBold</c> when available, otherwise falls back
        /// to substring matching on the (subset-prefix-stripped) font name.
        /// </summary>
        public bool IsBold => FirstFontDetails?.IsBold
            ?? FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || FontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
            || FontName.Contains("Black", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the line's font signals italic style. Reads
        /// <c>FontDetails.IsItalic</c> when available, otherwise falls back
        /// to substring matching for "Italic" or "Oblique" in the font name.
        /// </summary>
        public bool IsItalic => FirstFontDetails?.IsItalic
            ?? FontName.Contains("Italic", StringComparison.OrdinalIgnoreCase)
            || FontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Numeric font weight from <c>FontDetails.Weight</c>, or 400/700
        /// derived from <see cref="IsBold"/> as a binary fallback when no
        /// font descriptor is available.
        /// </summary>
        public int FontWeight => FirstFontDetails?.Weight ?? (IsBold ? 700 : 400);

        /// <summary>Visible text of the line, with word boundaries reordered left-to-right and joined by spaces.</summary>
        public string Text => string.Join(" ", _words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));

        /// <summary>Axis-aligned line bounding box derived from the per-word extents.</summary>
        public Models.BoundingBox Bbox => new(Left, Bottom, Right, Top);

        /// <summary>Materialises the accumulated state as an immutable <see cref="TextLineBlock"/>.</summary>
        public TextLineBlock ToTextLineBlock() => new(
            Bbox,
            Text,
            FontName,
            AvgFontSize,
            IsBold,
            BaselineY,
            AvgHeight,
            IsItalic: IsItalic,
            FontWeight: FontWeight);
    }
}

// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Per-signal breakdown of a block's heading probability. Useful for
/// calibration: emitting these to CSV reveals which signals discriminate
/// headings from body text on a given fixture and whether the threshold
/// produces clean separations.
/// </summary>
/// <param name="Neighbour">
/// Score from the prev/next neighbour comparison, in <c>[0, 1]</c>. When
/// the previous block is itself a heading, only the next-neighbour score
/// is used; otherwise the lower of the two is taken so that a candidate
/// must look heading-like against <em>both</em> sides.
/// </param>
/// <param name="Initial">Bonus added when the block is the document's first non-stats-only entry.</param>
/// <param name="Standalone">Bonus added when no other block on the page overlaps the candidate's row.</param>
/// <param name="CenterAligned">
/// Bonus added when the block is visually centred on its page (both side
/// margins substantial and approximately equal). Captures display-style
/// titles like a centred document title at the top of page 1.
/// </param>
/// <param name="VerticalGap">Bonus added when the candidate has unusually large whitespace above or below it (within the same page).</param>
/// <param name="AllCaps">Bonus added when every cased letter in the block is uppercase (Latin script only — CJK has no case and is skipped).</param>
/// <param name="NextPagePenalty">
/// Negative contribution applied when the next non-stats-only block is on a
/// different page. Catches surviving running headers/footers whose natural
/// successor sits across a page break.
/// </param>
/// <param name="FontSizeRarity">Font-size rarity rank, weighted by the classifier's size weight.</param>
/// <param name="FontWeightRarity">Font-weight rarity rank, weighted by the classifier's weight weight.</param>
/// <param name="Bulleted">Bonus added when the block begins with a list-label glyph.</param>
/// <param name="LineDecay">
/// Multiplicative decay applied to the summed signals based on line count.
/// Computed as <c>max(0, 1 - 0.0291·(lineCount - 1)²)</c>: a 1-line block
/// is unaffected, a 5-line block keeps about 53% of the score, and a
/// 7-or-more-line block is crushed to zero.
/// </param>
/// <param name="SentenceFlowDemotion">
/// Multiplicative penalty applied when the previous block ends mid-sentence,
/// signalling that the candidate is sandwiched inside body flow rather than
/// breaking it. Catches magazine pull-quotes that interrupt a paragraph
/// visually but not syntactically. <c>1.0</c> when no demotion applies.
/// </param>
/// <param name="Total">Final probability, clamped to <c>[0, 1]</c>; classification compares this against the threshold.</param>
public readonly record struct HeadingProbabilityBreakdown(
    double Neighbour,
    double Initial,
    double Standalone,
    double CenterAligned,
    double VerticalGap,
    double AllCaps,
    double NextPagePenalty,
    double FontSizeRarity,
    double FontWeightRarity,
    double Bulleted,
    double LineDecay,
    double SentenceFlowDemotion,
    double Total);

/// <summary>
/// One entry of a document-wide heading-probability analysis: the breakdown
/// for a single non-stats-only block, paired with its index in the supplied
/// document sequence and the threshold-based classification outcome.
/// </summary>
/// <param name="Index">Index into the original document sequence supplied to the classifier.</param>
/// <param name="Breakdown">Per-signal contributions and the summed total.</param>
/// <param name="ClassifiedAsHeading">Whether the total exceeds the configured threshold.</param>
public readonly record struct HeadingScoreEntry(
    int Index,
    HeadingProbabilityBreakdown Breakdown,
    bool ClassifiedAsHeading);

/// <summary>
/// Defines a classifier that determines the semantic type of text blocks.
/// </summary>
/// <remarks>
/// Receives every block in the document in reading order, with page numbers
/// carried on each <see cref="DocumentTextBlock"/>. Implementations are
/// expected to be stateless — any document-wide statistics they need (font
/// frequency, neighbour relationships, etc.) are computed from the supplied
/// sequence on each invocation. This rules out hidden cache-versus-input
/// mismatches and makes classifiers safe to share across parses.
/// </remarks>
public interface IElementClassifier
{
    /// <summary>
    /// Classifies the document's text blocks into typed content elements,
    /// in input order. Implementations may consult neighbouring entries in
    /// <paramref name="documentBlocks"/> for context.
    /// </summary>
    /// <param name="documentBlocks">Every block extracted from the document, in reading order, paired with its 1-indexed page number.</param>
    /// <param name="startId">The next element ID to assign; advanced by one per produced element.</param>
    /// <returns>One <see cref="ContentElement"/> per input block, in the same order.</returns>
    IReadOnlyList<ContentElement> Classify(
        IReadOnlyList<DocumentTextBlock> documentBlocks, ref int startId);
}

/// <summary>
/// Classifies text blocks into headings and paragraphs by combining a
/// neighbour-relative comparison (font weight and size against the previous
/// and next non-stats-only blocks) with document-wide rarity boosts on font
/// size and weight, an initial-heading bonus for the first substantive
/// block, a standalone-row bonus, and a multiplicative decay against line
/// count.
/// </summary>
/// <remarks>
/// The probability model is intentionally language-agnostic — every signal
/// is layout- or typography-derived, no text patterns are inspected.
/// Documents whose section headers carry no typographic distinction (e.g.
/// legal corpora that rely solely on textual markers like <c>제1장</c>) need
/// a domain-specific classifier composed in front of this one via
/// <see cref="CompositeElementClassifier"/>.
/// </remarks>
public sealed class FontBasedElementClassifier : IElementClassifier
{
    private const double InitialHeadingBoost = 0.27;
    private const double StandaloneBoost = 0.15;
    private const double CenterAlignedBoost = 0.30;
    private const double VerticalGapBoost = 0.20;
    private const double VerticalGapThresholdRatio = 0.5;
    private const double AllCapsBoost = 0.15;
    private const double NextPagePenalty = -0.50;
    private const double SentenceFlowDemotionMultiplier = 0.30;
    private const double LineDecayCoefficient = 0.0291;

    private readonly double _headingProbabilityThreshold;
    private readonly double _fontSizeRarityWeight;
    private readonly double _fontWeightRarityWeight;
    private readonly double _bulletedBoost;

    /// <summary>
    /// Initializes a new instance of <see cref="FontBasedElementClassifier"/>.
    /// </summary>
    /// <param name="headingProbabilityThreshold">
    /// Probability above which a block is classified as a heading. Tune
    /// downward to recall more headings or upward to reduce false positives.
    /// Default <c>0.75</c>.
    /// </param>
    public FontBasedElementClassifier(double headingProbabilityThreshold = 0.75)
        : this(headingProbabilityThreshold, fontSizeRarityWeight: 0.5, fontWeightRarityWeight: 0.3, bulletedBoost: 0.1)
    {
    }

    /// <summary>
    /// Initializes a new instance with explicit weights for each signal.
    /// </summary>
    /// <param name="headingProbabilityThreshold">Threshold above which a block is classified as a heading.</param>
    /// <param name="fontSizeRarityWeight">Multiplier applied to the font-size rarity rank before summation.</param>
    /// <param name="fontWeightRarityWeight">Multiplier applied to the font-weight rarity rank before summation.</param>
    /// <param name="bulletedBoost">Constant boost added when the block begins with a recognised list-label glyph.</param>
    public FontBasedElementClassifier(
        double headingProbabilityThreshold,
        double fontSizeRarityWeight,
        double fontWeightRarityWeight,
        double bulletedBoost)
    {
        _headingProbabilityThreshold = headingProbabilityThreshold;
        _fontSizeRarityWeight = fontSizeRarityWeight;
        _fontWeightRarityWeight = fontWeightRarityWeight;
        _bulletedBoost = bulletedBoost;
    }

    /// <summary>The probability threshold above which a block is classified as a heading.</summary>
    public double HeadingProbabilityThreshold => _headingProbabilityThreshold;

    /// <inheritdoc />
    public IReadOnlyList<ContentElement> Classify(
        IReadOnlyList<DocumentTextBlock> documentBlocks, ref int startId)
    {
        var stats = new DocumentStatistics(documentBlocks.Select(d => d.Block));
        var initialIndex = FindFirstNonStatsOnlyIndex(documentBlocks);
        var classifiedAsHeading = ScoreAndClassify(documentBlocks, stats, initialIndex);
        DemoteConsecutiveSameStyleHeadings(classifiedAsHeading, documentBlocks);

        var elements = new List<ContentElement>(documentBlocks.Count);
        for (var i = 0; i < documentBlocks.Count; i++)
        {
            var entry = documentBlocks[i];
            if (entry.IsStatsOnly) continue;
            elements.Add(classifiedAsHeading[i]
                ? CreateHeading(entry.Block, entry.PageNumber, ref startId)
                : CreateParagraph(entry.Block, entry.PageNumber, ref startId));
        }
        return elements;
    }

    /// <summary>
    /// Pass 1 — scores every non-stats-only block in reading order and marks
    /// those above threshold as headings. The boolean array returned uses
    /// indices into <paramref name="documentBlocks"/>; stats-only entries
    /// remain <c>false</c>.
    /// </summary>
    private bool[] ScoreAndClassify(
        IReadOnlyList<DocumentTextBlock> documentBlocks,
        DocumentStatistics stats,
        int initialIndex)
    {
        var classifiedAsHeading = new bool[documentBlocks.Count];
        for (var i = 0; i < documentBlocks.Count; i++)
        {
            if (documentBlocks[i].IsStatsOnly) continue;
            var breakdown = ComputeBreakdown(i, documentBlocks, classifiedAsHeading, initialIndex, stats);
            classifiedAsHeading[i] = breakdown.Total > _headingProbabilityThreshold;
        }
        return classifiedAsHeading;
    }

    /// <summary>
    /// Pass 2 — demotes consecutive heading runs that share a typographic
    /// style. Real heading hierarchies use distinct styles for distinct
    /// levels (chapter big, section smaller); a sequence of similarly-styled
    /// "headings" with no body between them is more often a metadata cluster
    /// (paper cover-page authors, affiliations, citations) that the
    /// per-block scoring failed to discriminate. Keeps the first member of
    /// each run, demotes the rest. The chain is broken by any substantial
    /// body block — a multi-line paragraph or a single line longer than
    /// roughly half a body line.
    /// </summary>
    private static void DemoteConsecutiveSameStyleHeadings(
        bool[] classifiedAsHeading,
        IReadOnlyList<DocumentTextBlock> documentBlocks)
    {
        TextBlock? lastHeadingBlock = null;
        for (var i = 0; i < documentBlocks.Count; i++)
        {
            if (documentBlocks[i].IsStatsOnly) continue;

            var block = documentBlocks[i].Block;
            if (classifiedAsHeading[i])
            {
                if (lastHeadingBlock is { } previous && IsSameHeadingStyle(block, previous))
                {
                    classifiedAsHeading[i] = false;
                }
                else
                {
                    lastHeadingBlock = block;
                }
            }
            else if (IsSubstantialBody(block))
            {
                lastHeadingBlock = null;
            }
        }
    }

    /// <summary>
    /// Two heading-classified blocks count as the same style when their
    /// font weight matches and their sizes are within 1.5 points. Font
    /// face is intentionally not part of the test — embedded fonts on
    /// cover pages frequently mix related faces (Bold and BoldCondensed,
    /// for example) for visually equivalent metadata.
    /// </summary>
    private static bool IsSameHeadingStyle(TextBlock a, TextBlock b)
    {
        if (a.IsBold != b.IsBold) return false;
        return Math.Abs(a.FontSize - b.FontSize) <= 1.5;
    }

    /// <summary>
    /// Returns <c>true</c> when a paragraph block is "substantial enough"
    /// to break a same-style heading run. Multi-line paragraphs always
    /// qualify; single-line paragraphs need to carry a recognisable amount
    /// of running text rather than a stray label or marker.
    /// </summary>
    private static bool IsSubstantialBody(TextBlock block) =>
        block.LineCount >= 2 || block.Text.Trim().Length > 60;

    /// <summary>
    /// Runs the neighbour-aware classification pass without producing
    /// <see cref="ContentElement"/>s, returning the per-block breakdowns
    /// alongside the threshold-based classification outcome. Intended for
    /// calibration tooling — diagnose this output to understand which
    /// signals discriminate headings on a given fixture.
    /// </summary>
    /// <param name="documentBlocks">The same document sequence supplied to <see cref="Classify"/>.</param>
    /// <returns>One entry per non-stats-only block, in input order.</returns>
    public IReadOnlyList<HeadingScoreEntry> AnalyzeHeadings(
        IReadOnlyList<DocumentTextBlock> documentBlocks)
    {
        var stats = new DocumentStatistics(documentBlocks.Select(d => d.Block));
        var classifiedAsHeading = new bool[documentBlocks.Count];
        var initialIndex = FindFirstNonStatsOnlyIndex(documentBlocks);
        var breakdowns = new HeadingProbabilityBreakdown[documentBlocks.Count];

        for (var i = 0; i < documentBlocks.Count; i++)
        {
            if (documentBlocks[i].IsStatsOnly) continue;

            breakdowns[i] = ComputeBreakdown(i, documentBlocks, classifiedAsHeading, initialIndex, stats);
            classifiedAsHeading[i] = breakdowns[i].Total > _headingProbabilityThreshold;
        }

        DemoteConsecutiveSameStyleHeadings(classifiedAsHeading, documentBlocks);

        var entries = new List<HeadingScoreEntry>(documentBlocks.Count);
        for (var i = 0; i < documentBlocks.Count; i++)
        {
            if (documentBlocks[i].IsStatsOnly) continue;
            entries.Add(new HeadingScoreEntry(i, breakdowns[i], classifiedAsHeading[i]));
        }
        return entries;
    }

    /// <summary>
    /// Computes the per-signal breakdown of the candidate at <paramref name="index"/>
    /// against its neighbours. The previous block's classification (already
    /// determined by the left-to-right pass) flips the neighbour comparison
    /// into a next-only check: when the previous block is already a heading,
    /// the candidate is not re-compared against it.
    /// </summary>
    private HeadingProbabilityBreakdown ComputeBreakdown(
        int index,
        IReadOnlyList<DocumentTextBlock> documentBlocks,
        IReadOnlyList<bool> classifiedAsHeading,
        int initialIndex,
        DocumentStatistics stats)
    {
        var currentEntry = documentBlocks[index];
        var current = currentEntry.Block;
        var prevIndex = FindPrevNonStatsOnly(documentBlocks, index);
        var nextIndex = FindNextNonStatsOnly(documentBlocks, index);
        var prevIsHeading = prevIndex >= 0 && classifiedAsHeading[prevIndex];

        if (prevIsHeading && nextIndex < 0)
        {
            // Tail-block rule: when the previous block is already a heading
            // and there is no next neighbour to compare against, refuse to
            // chain another heading at the document's tail. Applies regardless
            // of how heading-like the candidate looks — the chain has nowhere
            // to extend, and a body-like candidate could still pick up a
            // positive prev_score against the immediately-preceding heading
            // without the rule firing.
            return new HeadingProbabilityBreakdown(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0);
        }

        // The prev-is-heading shortcut (compare only against the next
        // neighbour) only makes sense when the candidate is itself
        // typographically distinct enough to be a heading. Without this guard,
        // body text immediately following a heading inherits a strong
        // next-neighbour score against the next paragraph and cascades into a
        // false positive. Treat the candidate as heading-like only when it is
        // bold or substantially larger than the document's body baseline —
        // additive thresholds are too lenient on dense cover pages where the
        // mode is small (e.g. PLOS papers at 8pt) and a 10pt regular body
        // paragraph would qualify as "heading-like" by a +0.5 margin.
        var headingLike = current.IsBold
            || (stats.ModeFontSize > 0 && current.FontSize >= stats.ModeFontSize * 1.3);

        double neighbourScore;
        if (prevIsHeading && headingLike)
        {
            neighbourScore = ScoreVsNeighbour(current, documentBlocks[nextIndex].Block);
        }
        else
        {
            var prevScore = prevIndex >= 0 ? ScoreVsNeighbour(current, documentBlocks[prevIndex].Block) : 1.0;
            var nextScore = nextIndex >= 0 ? ScoreVsNeighbour(current, documentBlocks[nextIndex].Block) : 1.0;
            neighbourScore = Math.Min(prevScore, nextScore);
        }

        var initialBoost = index == initialIndex && initialIndex >= 0 ? InitialHeadingBoost : 0.0;
        var standaloneBoost = current.IsStandalone ? StandaloneBoost : 0.0;
        var centerAlignedBoost = DocumentLayoutStatistics.IsCenterAligned(current, currentEntry.PageWidth)
            ? CenterAlignedBoost : 0.0;
        var verticalGapBoost = ComputeVerticalGapBoost(currentEntry, documentBlocks, prevIndex, nextIndex);
        var allCapsBoost = IsAllCaps(current.Text) ? AllCapsBoost : 0.0;
        var nextPagePenalty = nextIndex >= 0 && documentBlocks[nextIndex].PageNumber != currentEntry.PageNumber
            ? NextPagePenalty : 0.0;
        var sizeRarityBoost = stats.FontSizeRarity.GetBoost(stats.RoundFontSize(current.FontSize)) * _fontSizeRarityWeight;
        var weightRarityBoost = stats.FontWeightRarity.GetBoost(current.FontWeight) * _fontWeightRarityWeight;
        var bulletedBoost = IsBulleted(current.Text) ? _bulletedBoost : 0.0;

        var sum = neighbourScore + initialBoost + standaloneBoost + centerAlignedBoost
                + verticalGapBoost + allCapsBoost + nextPagePenalty
                + sizeRarityBoost + weightRarityBoost + bulletedBoost;
        var lineDecay = LineCountDecay(current.LineCount);
        var sentenceFlowDemotion = ComputeSentenceFlowDemotion(current, currentEntry, documentBlocks, prevIndex, classifiedAsHeading);
        var total = Math.Clamp(sum * lineDecay * sentenceFlowDemotion, 0.0, 1.0);

        return new HeadingProbabilityBreakdown(
            Neighbour: neighbourScore,
            Initial: initialBoost,
            Standalone: standaloneBoost,
            CenterAligned: centerAlignedBoost,
            VerticalGap: verticalGapBoost,
            AllCaps: allCapsBoost,
            NextPagePenalty: nextPagePenalty,
            FontSizeRarity: sizeRarityBoost,
            FontWeightRarity: weightRarityBoost,
            Bulleted: bulletedBoost,
            LineDecay: lineDecay,
            SentenceFlowDemotion: sentenceFlowDemotion,
            Total: total);
    }

    /// <summary>
    /// Adds a fixed boost when the candidate has unusually large whitespace
    /// either above (gap to <paramref name="prevIndex"/>) or below (gap to
    /// <paramref name="nextIndex"/>) within the same page. "Unusually large"
    /// is defined relative to the candidate's font size: a gap exceeding
    /// half the font size is treated as the kind of paragraph-break-or-more
    /// whitespace that surrounds headings. Only same-page neighbours count
    /// — the gap across a page break is dominated by margins, not content.
    /// </summary>
    private static double ComputeVerticalGapBoost(
        DocumentTextBlock currentEntry,
        IReadOnlyList<DocumentTextBlock> documentBlocks,
        int prevIndex,
        int nextIndex)
    {
        var current = currentEntry.Block;
        if (current.FontSize <= 0) return 0.0;
        var threshold = current.FontSize * VerticalGapThresholdRatio;

        if (prevIndex >= 0 && documentBlocks[prevIndex].PageNumber == currentEntry.PageNumber)
        {
            var prev = documentBlocks[prevIndex].Block;
            var gapAbove = prev.BoundingBox.Bottom - current.BoundingBox.Top;
            if (gapAbove > threshold) return VerticalGapBoost;
        }

        if (nextIndex >= 0 && documentBlocks[nextIndex].PageNumber == currentEntry.PageNumber)
        {
            var next = documentBlocks[nextIndex].Block;
            var gapBelow = current.BoundingBox.Bottom - next.BoundingBox.Top;
            if (gapBelow > threshold) return VerticalGapBoost;
        }

        return 0.0;
    }

    /// <summary>
    /// Returns a multiplier in <c>(0, 1]</c> demoting candidates that sit
    /// inside body flow. The signal targets the magazine pull-quote pattern:
    /// a multi-line display-type block embedded inside a paragraph that
    /// continues across it. Demotion fires only when every guard passes,
    /// otherwise returns <c>1.0</c>. Guards:
    /// <list type="bullet">
    ///   <item><description><c>prev</c> exists, on the same page, and was not classified as a heading.</description></item>
    ///   <item><description><c>prev</c> is multi-line body — a one-line prev is far more likely a title or fragment than a paragraph the candidate could be sandwiched inside.</description></item>
    ///   <item><description><c>current</c> itself is multi-line (≥ 3 lines). Pull quotes are visually substantial; a one- or two-line candidate that follows an unterminated paragraph is more often a real heading whose neighbour just happens to lack a terminator (e.g. a preamble that ends without a period in the source PDF).</description></item>
    ///   <item><description><c>prev</c>'s last line ends mid-sentence — no Korean terminator and no Latin period/colon/etc.</description></item>
    /// </list>
    /// </summary>
    private static double ComputeSentenceFlowDemotion(
        TextBlock current,
        DocumentTextBlock currentEntry,
        IReadOnlyList<DocumentTextBlock> documentBlocks,
        int prevIndex,
        IReadOnlyList<bool> classifiedAsHeading)
    {
        if (prevIndex < 0) return 1.0;
        if (documentBlocks[prevIndex].PageNumber != currentEntry.PageNumber) return 1.0;
        if (classifiedAsHeading[prevIndex]) return 1.0;
        if (current.LineCount < 3) return 1.0;

        var prev = documentBlocks[prevIndex].Block;
        if (prev.LineCount < 2) return 1.0;

        var prevLastLine = LastLine(prev.Text);
        return SentenceFlow.IsLineContinuation(prevLastLine)
            ? SentenceFlowDemotionMultiplier
            : 1.0;
    }

    /// <summary>Returns the last newline-delimited segment of a block's text, trimmed of trailing whitespace.</summary>
    private static string LastLine(string text)
    {
        var newline = text.LastIndexOf('\n');
        return (newline >= 0 ? text[(newline + 1)..] : text).TrimEnd();
    }

    /// <summary>
    /// Returns <c>true</c> when the block contains at least three cased
    /// letters and every cased letter is uppercase. Cased means the
    /// character has a meaningful upper/lower distinction — Hangul, CJK
    /// ideographs, and other case-less scripts are skipped, so a Korean
    /// block is never reported as ALL CAPS.
    /// </summary>
    private static bool IsAllCaps(string text)
    {
        var cased = 0;
        var upper = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (!char.IsUpper(c) && !char.IsLower(c)) continue;
            cased++;
            if (char.IsUpper(c)) upper++;
        }
        return cased >= 3 && upper == cased;
    }

    /// <summary>
    /// Returns a <c>[0, 1]</c> score expressing how heading-like the
    /// candidate looks against a single neighbour. Combines two signals: a
    /// flat <c>+0.5</c> when the candidate is bold and the neighbour is not
    /// (the cleanest typographic distinction headings carry against body
    /// text in most corpora) and a size-ratio bonus that grows linearly
    /// from <c>+0</c> at parity to <c>+0.5</c> at a 33%+ size increase.
    /// </summary>
    private static double ScoreVsNeighbour(TextBlock current, TextBlock neighbour)
    {
        double score = 0.0;

        if (current.IsBold && !neighbour.IsBold)
            score += 0.5;

        if (neighbour.FontSize > 0 && current.FontSize > neighbour.FontSize)
        {
            var ratio = (current.FontSize - neighbour.FontSize) / neighbour.FontSize;
            score += Math.Min(0.5, ratio * 1.5);
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// Computes the multiplicative line-count decay. Single-line blocks
    /// pass through unchanged; multi-line blocks are progressively crushed
    /// by <c>max(0, 1 - k·(n-1)²)</c> where <c>k = LineDecayCoefficient</c>.
    /// The quadratic shape drives the score to zero at the tenth line, so
    /// long paragraphs cannot accumulate heading probability no matter how
    /// strong their typography signal is.
    /// </summary>
    private static double LineCountDecay(int lineCount)
    {
        if (lineCount <= 1) return 1.0;
        var deficit = lineCount - 1;
        return Math.Max(0.0, 1.0 - LineDecayCoefficient * deficit * deficit);
    }

    /// <summary>Returns the index of the nearest preceding non-stats-only block, or <c>-1</c> when none exists.</summary>
    private static int FindPrevNonStatsOnly(IReadOnlyList<DocumentTextBlock> documentBlocks, int index)
    {
        for (var i = index - 1; i >= 0; i--)
            if (!documentBlocks[i].IsStatsOnly) return i;
        return -1;
    }

    /// <summary>Returns the index of the nearest following non-stats-only block, or <c>-1</c> when none exists.</summary>
    private static int FindNextNonStatsOnly(IReadOnlyList<DocumentTextBlock> documentBlocks, int index)
    {
        for (var i = index + 1; i < documentBlocks.Count; i++)
            if (!documentBlocks[i].IsStatsOnly) return i;
        return -1;
    }

    /// <summary>Returns the index of the document's first non-stats-only block, or <c>-1</c> when the input has none.</summary>
    private static int FindFirstNonStatsOnlyIndex(IReadOnlyList<DocumentTextBlock> documentBlocks)
    {
        for (var i = 0; i < documentBlocks.Count; i++)
            if (!documentBlocks[i].IsStatsOnly) return i;
        return -1;
    }

    /// <summary>
    /// Returns <c>true</c> when a block begins with a list-label glyph
    /// (ASCII bullets, geometric shapes, circled numerals). Matches a
    /// trimmed prefix of common bullet symbols; the table is intentionally
    /// small and can be extended when new corpora demand it.
    /// </summary>
    private static bool IsBulleted(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0) return false;
        return s_bulletGlyphs.IndexOf(trimmed[0]) >= 0;
    }

    /// <summary>
    /// A pragmatic subset of bullet/list-label glyphs covering ASCII,
    /// geometric, arrow, and circled-numeral families.
    /// </summary>
    private static readonly string s_bulletGlyphs =
        "-*+•·○●■□▪▫▶▷◆◇★☆→⇒"
      + "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳"
      + "❶❷❸❹❺❻❼❽❾❿";

    /// <summary>
    /// Creates a <see cref="HeadingElement"/> for the given block. The heading
    /// level is left unassigned (<c>0</c>) and the label empty so the
    /// downstream <c>AssignHeadingLevels</c> pass can cluster the heading
    /// alongside its style siblings. A regex- or pattern-driven classifier
    /// that knows the heading's hierarchical level emits non-zero values
    /// directly; <c>AssignHeadingLevels</c> preserves those.
    /// </summary>
    private static HeadingElement CreateHeading(TextBlock block, int pageNumber, ref int id) => new()
    {
        Id = id++,
        PageNumber = pageNumber,
        BoundingBox = block.BoundingBox,
        HeadingLevel = 0,
        Level = string.Empty,
        Text = ToTextProperties(block)
    };

    /// <summary>Creates a <see cref="ParagraphElement"/> for the given block.</summary>
    private static ParagraphElement CreateParagraph(TextBlock block, int pageNumber, ref int id) => new()
    {
        Id = id++,
        PageNumber = pageNumber,
        BoundingBox = block.BoundingBox,
        Text = ToTextProperties(block)
    };

    /// <summary>Builds the <see cref="TextProperties"/> payload from a block.</summary>
    private static TextProperties ToTextProperties(TextBlock block) => new()
    {
        Font = block.FontName,
        FontSize = block.FontSize,
        Content = block.Text.Trim()
    };
}

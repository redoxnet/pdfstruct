// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.
//
// Phase 1 list detection. Implementation written from
// docs/list-detection-spec.md only; upstream Java sources were not
// consulted during coding (see docs/adr/0001-list-detection-license.md).

using System.Globalization;
using System.Text.RegularExpressions;
using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// A parsed Arabic-numeric label decomposed into its three textual parts.
/// </summary>
/// <param name="Prefix">Characters appearing before the digit run (empty, <c>(</c>, or <c>[</c>).</param>
/// <param name="Number">The integer recovered from the digit run.</param>
/// <param name="Terminator">Single non-digit character that terminates the label (e.g. <c>.</c>, <c>)</c>, <c>]</c>, <c>:</c>, <c>;</c>).</param>
/// <param name="DigitText">The raw digit run exactly as printed, preserving leading zeros (e.g. <c>0001</c>); the source of truth for the original label token.</param>
internal readonly record struct ListLabel(string Prefix, int Number, char Terminator, string DigitText);

/// <summary>
/// One member of a detected list run.
/// </summary>
/// <param name="Number">The integer label number recovered from the start line.</param>
/// <param name="Body">Item body text — the start line's content with the label prefix stripped, joined to absorbed continuation lines with newlines.</param>
/// <param name="BoundingBox">Union of the start line and every absorbed continuation line.</param>
/// <param name="StartLineIndex">Index of the start line within the page's text-line sequence.</param>
/// <param name="BodyLineIndices">Page-line indices absorbed as body continuation of the item (the start line plus any continuation lines).</param>
/// <param name="ChildrenLineIndices">Page-line indices absorbed as children of the item. Phase 2 territory walk; empty for the last item of any list.</param>
/// <param name="RawLabel">The original label token exactly as printed, including delimiters and leading zeros (e.g. <c>[0001]</c>, <c>[6]</c>, <c>6.</c>).</param>
internal sealed record DetectedListItem(
    int Number,
    string Body,
    BoundingBox BoundingBox,
    int StartLineIndex,
    IReadOnlyList<int> BodyLineIndices,
    IReadOnlyList<int> ChildrenLineIndices,
    string RawLabel)
{
    /// <summary>Every page-line index owned by this item, body and children combined, in original document order.</summary>
    public IEnumerable<int> ClaimedLineIndices =>
        BodyLineIndices.Concat(ChildrenLineIndices).OrderBy(i => i);
}

/// <summary>
/// A confirmed run of two or more list items sharing a common label shape.
/// </summary>
/// <param name="CommonPrefix">Prefix string shared by every item's label.</param>
/// <param name="Terminator">The label-terminating glyph shared by every item.</param>
/// <param name="Items">Items in document order.</param>
/// <param name="BoundingBox">Union of every item's bounding box.</param>
/// <param name="FontSize">Font size of the first item's start line, propagated for downstream layout reasoning.</param>
/// <param name="FontName">Font name of the first item's start line.</param>
internal sealed record DetectedList(
    string CommonPrefix,
    char Terminator,
    IReadOnlyList<DetectedListItem> Items,
    BoundingBox BoundingBox,
    double FontSize,
    string FontName);

/// <summary>
/// Per-page output of <see cref="ListDetector.Detect"/>.
/// </summary>
/// <param name="Lists">Confirmed list runs in document order.</param>
/// <param name="ResidualLines">The page's text lines with claimed lines removed and document order preserved.</param>
/// <param name="ClaimedOriginalIndices">Original-index set of every line claimed by some list (for downstream cross-checking).</param>
internal sealed record ListDetectionResult(
    IReadOnlyList<DetectedList> Lists,
    IReadOnlyList<TextLineBlock> ResidualLines,
    IReadOnlySet<int> ClaimedOriginalIndices);

/// <summary>
/// Detects Arabic-numeric ordered lists in a page's text-line stream
/// before paragraph merging. See <c>docs/list-detection-spec.md</c> for the
/// authoritative behaviour specification; this class is the C# realisation
/// of that specification.
/// </summary>
internal static class ListDetector
{
    private const int MaxCandidateLookback = 500;
    private const double NearLeftToleranceMultiplier = 4.0;
    private const double InterLineSpacingMultiplier = 2.0;

    /// <summary>
    /// A line whose font size exceeds the item's start line by more than this
    /// factor ends the item's territory: it is a heading or section break, not
    /// continuation. Guards numbered-paragraph runs (patent <c>[0001]</c>...)
    /// from swallowing an intervening section heading such as
    /// <c>배 경 기 술</c>.
    /// </summary>
    private const double HeadingFontSizeRatio = 1.1;

    private static readonly Regex s_parenLabel = new(
        @"^\(\s*(\d+)\s*\)\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex s_bracketLabel = new(
        @"^\[\s*(\d+)\s*\]\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex s_trailingLabel = new(
        @"^(\d+)([.):;])\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex s_decimalLabel = new(
        @"^\d+\.\d+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Detects Arabic-numeric lists in a single page's text lines. The
    /// caller is responsible for sequencing this between the running
    /// furniture filter and the paragraph merger.
    /// </summary>
    /// <param name="pageLines">Text lines for one page in document order.</param>
    /// <returns>Confirmed lists, the residual line stream, and the set of claimed line indices.</returns>
    public static ListDetectionResult Detect(IReadOnlyList<TextLineBlock> pageLines)
    {
        if (pageLines.Count == 0)
            return EmptyResult(pageLines);

        var candidates = new List<Candidate>();
        for (var i = 0; i < pageLines.Count; i++)
        {
            var line = pageLines[i];
            if (string.IsNullOrWhiteSpace(line.Text) || line.FontSize < 1.0)
                continue;

            var parsed = TryParseLabel(line.Text);
            if (parsed is null)
                continue;

            var (label, labelLength) = parsed.Value;
            var matched = FindExtensibleCandidate(candidates, line, label);
            if (matched is not null)
                matched.Append(i, line, label, labelLength);
            else
                candidates.Add(new Candidate(i, line, label, labelLength));
        }

        var lists = new List<DetectedList>();
        var claimed = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            if (candidate.ItemCount < 2) continue;
            if (candidate.AllLabelsLookLikeDecimals(s_decimalLabel)) continue;

            AbsorbTerritories(candidate, pageLines, claimed);
            foreach (var lineIndex in candidate.AllClaimedLineIndices()) claimed.Add(lineIndex);
            lists.Add(candidate.ToDetectedList());
        }

        lists.Sort((a, b) => a.Items[0].StartLineIndex.CompareTo(b.Items[0].StartLineIndex));

        var residual = new List<TextLineBlock>(pageLines.Count - claimed.Count);
        for (var i = 0; i < pageLines.Count; i++)
            if (!claimed.Contains(i))
                residual.Add(pageLines[i]);

        return new ListDetectionResult(lists, residual, claimed);
    }

    /// <summary>
    /// Attempts to parse the leading text of a line as one of the three
    /// recognised Arabic-numeric label shapes. Returns the parsed label and
    /// the character length of the matched label text (used to strip the
    /// label from the body), or <c>null</c> when no shape matches or when
    /// the leading digits look like a decimal value.
    /// </summary>
    public static (ListLabel Label, int MatchLength)? TryParseLabel(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var paren = s_parenLabel.Match(text);
        if (paren.Success)
        {
            var n = ParseInt(paren.Groups[1].Value);
            if (n is null) return null;
            return (new ListLabel("(", n.Value, ')', paren.Groups[1].Value), paren.Length);
        }

        var bracket = s_bracketLabel.Match(text);
        if (bracket.Success)
        {
            var n = ParseInt(bracket.Groups[1].Value);
            if (n is null) return null;
            return (new ListLabel("[", n.Value, ']', bracket.Groups[1].Value), bracket.Length);
        }

        var trailing = s_trailingLabel.Match(text);
        if (trailing.Success)
        {
            var n = ParseInt(trailing.Groups[1].Value);
            if (n is null) return null;
            var terminator = trailing.Groups[2].Value[0];
            return (new ListLabel(string.Empty, n.Value, terminator, trailing.Groups[1].Value), trailing.Length);
        }

        return null;
    }

    private static Candidate? FindExtensibleCandidate(
        List<Candidate> candidates,
        TextLineBlock line,
        ListLabel label)
    {
        var lookbackStart = Math.Max(0, candidates.Count - MaxCandidateLookback);
        for (var i = candidates.Count - 1; i >= lookbackStart; i--)
        {
            var candidate = candidates[i];
            if (candidate.CanExtend(line, label))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Walks each confirmed item's territory, absorbing lines as either
    /// body continuation (Phase 1 § 7) or as children (Phase 2 § 6). The
    /// last item of a list does only body absorption with no child zone,
    /// per Phase 2 § 8 — the territory of the last item is otherwise
    /// unbounded and risks over-absorbing post-list content.
    /// </summary>
    private static void AbsorbTerritories(
        Candidate candidate,
        IReadOnlyList<TextLineBlock> pageLines,
        HashSet<int> alreadyClaimed)
    {
        for (var k = 0; k < candidate.ItemCount; k++)
        {
            var item = candidate.GetMutableItem(k);
            var isLast = k == candidate.ItemCount - 1;
            var territoryEnd = isLast
                ? pageLines.Count
                : candidate.GetMutableItem(k + 1).LineIndex;

            var startLine = pageLines[item.LineIndex];
            var previousBaseline = startLine.BaselineY;
            var typicalSpacing = Math.Max(startLine.AvgHeight, 1.0) * InterLineSpacingMultiplier;
            var bodyMode = true;

            for (var j = item.LineIndex + 1; j < territoryEnd; j++)
            {
                if (alreadyClaimed.Contains(j)) break;

                var line = pageLines[j];
                var tol = SameLeftTolerance(line.FontSize);
                // A continuation may sit a marker-width to the LEFT of the item
                // start when the run is first-line-indented — patent claims print
                // "3." indented and wrap back to the column margin. Only a line
                // outdented well beyond that (different structure) ends the
                // territory; otherwise the wrap would orphan and merge into a
                // cross-claim blob.
                if (line.Left < startLine.Left - ContinuationLeftIndent(startLine.FontSize)) break;
                if (line.FontSize > startLine.FontSize * HeadingFontSizeRatio) break;
                // A label-looking line only ends the territory when it is left-
                // aligned with the item start — a genuine sibling/sub-item.
                // A label shape further right is continuation that merely opens
                // with digits-and-dot (a citation's trailing page number such as
                // "109. https://doi.org/..."); absorbing it keeps the reference
                // whole and stops a stray fragment from later voiding the list.
                if (line.Left <= startLine.Left + tol && TryParseLabel(line.Text) is not null) break;

                if (bodyMode)
                {
                    if (Math.Abs(line.BaselineY - previousBaseline) <= typicalSpacing)
                    {
                        item.AbsorbAsBody(line, j);
                        previousBaseline = line.BaselineY;
                        continue;
                    }

                    if (isLast)
                        break;

                    bodyMode = false;
                }

                item.AbsorbAsChild(line, j);
                previousBaseline = line.BaselineY;
            }
        }
    }

    private static double SameLeftTolerance(double fontSize) => Math.Max(fontSize, 1.0) / 3.0;

    /// <summary>
    /// Maximum distance, in points, a continuation line may sit to the left of
    /// its item's start before it counts as a structural outdent that ends the
    /// territory. Sized to a marker's first-line indent (≈2 em) so a claim's
    /// margin-aligned wrap is absorbed while a genuinely outdented block is not.
    /// </summary>
    private static double ContinuationLeftIndent(double fontSize) => Math.Max(fontSize, 1.0) * 2.0;

    private static int? ParseInt(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static ListDetectionResult EmptyResult(IReadOnlyList<TextLineBlock> lines) =>
        new([], lines, new HashSet<int>());

    /// <summary>
    /// Mutable per-candidate accumulator. A candidate represents an
    /// in-progress run of label-bearing lines that have so far matched the
    /// label-shape constraints. It graduates to a confirmed list when it
    /// holds at least two items and survives the decimal sanity filter.
    /// </summary>
    private sealed class Candidate
    {
        private readonly List<MutableItem> _items = [];
        private bool _everyTransitionWasStrictSameLeft = true;

        public Candidate(int lineIndex, TextLineBlock line, ListLabel label, int labelLength)
        {
            _items.Add(new MutableItem(lineIndex, line, label, labelLength));
        }

        public int ItemCount => _items.Count;

        public MutableItem GetMutableItem(int index) => _items[index];

        public bool CanExtend(TextLineBlock line, ListLabel label)
        {
            var last = _items[^1];
            if (last.Label.Prefix != label.Prefix) return false;
            if (last.Label.Terminator != label.Terminator) return false;
            if (label.Number != last.Label.Number + 1) return false;

            var tol = SameLeftTolerance(last.Line.FontSize);
            var diff = Math.Abs(line.Left - last.Line.Left);
            var sameLeft = diff <= tol;
            var nearLeft = diff <= NearLeftToleranceMultiplier * tol;

            if (_items.Count >= 2 && _everyTransitionWasStrictSameLeft && !sameLeft)
                return false;

            return sameLeft || nearLeft;
        }

        public void Append(int lineIndex, TextLineBlock line, ListLabel label, int labelLength)
        {
            var last = _items[^1];
            var tol = SameLeftTolerance(last.Line.FontSize);
            var sameLeft = Math.Abs(line.Left - last.Line.Left) <= tol;
            if (!sameLeft) _everyTransitionWasStrictSameLeft = false;

            _items.Add(new MutableItem(lineIndex, line, label, labelLength));
        }

        public bool AllLabelsLookLikeDecimals(Regex decimalPattern)
        {
            foreach (var item in _items)
                if (!decimalPattern.IsMatch(item.RawLabelToken))
                    return false;
            return _items.Count > 0;
        }

        public IEnumerable<int> AllClaimedLineIndices()
        {
            foreach (var item in _items)
                foreach (var idx in item.ClaimedLineIndices)
                    yield return idx;
        }

        public DetectedList ToDetectedList()
        {
            var first = _items[0];
            var commonPrefix = first.Label.Prefix;
            var terminator = first.Label.Terminator;

            var detectedItems = new List<DetectedListItem>(_items.Count);
            BoundingBox listBox = first.BoundingBox;

            foreach (var item in _items)
            {
                detectedItems.Add(new DetectedListItem(
                    Number: item.Label.Number,
                    Body: item.BodyText,
                    BoundingBox: item.BoundingBox,
                    StartLineIndex: item.LineIndex,
                    BodyLineIndices: item.BodyLineIndices,
                    ChildrenLineIndices: item.ChildrenLineIndices,
                    RawLabel: item.RawLabelToken));
                listBox = listBox.Merge(item.BoundingBox);
            }

            return new DetectedList(
                commonPrefix,
                terminator,
                detectedItems,
                listBox,
                FontSize: first.Line.FontSize,
                FontName: first.Line.FontName);
        }

    }

    /// <summary>
    /// Internal mutable representation of one item while its body and any
    /// children are being accumulated through the territory walk.
    /// </summary>
    private sealed class MutableItem
    {
        private readonly List<int> _bodyLineIndices = [];
        private readonly List<int> _childrenLineIndices = [];
        private readonly List<string> _bodyLines = [];
        private BoundingBox _boundingBox;

        public MutableItem(int lineIndex, TextLineBlock line, ListLabel label, int labelLength)
        {
            LineIndex = lineIndex;
            Line = line;
            Label = label;
            _boundingBox = line.BoundingBox;
            _bodyLineIndices.Add(lineIndex);

            var clamped = Math.Min(labelLength, line.Text.Length);
            var stripped = line.Text.Length > clamped ? line.Text[clamped..] : string.Empty;
            _bodyLines.Add(stripped);

            var digits = string.IsNullOrEmpty(label.DigitText)
                ? label.Number.ToString(CultureInfo.InvariantCulture)
                : label.DigitText;
            RawLabelToken = string.Concat(
                label.Prefix,
                digits,
                label.Terminator == '\0' ? string.Empty : label.Terminator.ToString());
        }

        public int LineIndex { get; }
        public TextLineBlock Line { get; }
        public ListLabel Label { get; }
        public string RawLabelToken { get; }
        public BoundingBox BoundingBox => _boundingBox;
        public IReadOnlyList<int> BodyLineIndices => _bodyLineIndices;
        public IReadOnlyList<int> ChildrenLineIndices => _childrenLineIndices;
        public string BodyText => string.Join("\n", _bodyLines);

        /// <summary>Every page-line index owned by this item, in original document order.</summary>
        public IEnumerable<int> ClaimedLineIndices => _bodyLineIndices.Concat(_childrenLineIndices);

        public void AbsorbAsBody(TextLineBlock continuation, int lineIndex)
        {
            _bodyLines.Add(continuation.Text);
            _boundingBox = _boundingBox.Merge(continuation.BoundingBox);
            _bodyLineIndices.Add(lineIndex);
        }

        public void AbsorbAsChild(TextLineBlock childLine, int lineIndex)
        {
            _boundingBox = _boundingBox.Merge(childLine.BoundingBox);
            _childrenLineIndices.Add(lineIndex);
        }
    }
}
